using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Options;
using XmppDotNet;
using XmppDotNet.Transport;
using XmppDotNet.Transport.WebSocket;
using XmppDotNet.Xml;
using XmppDotNet.Xmpp;
using XmppDotNet.Xmpp.Client;

namespace chat.Services
{
    /// <summary>
    /// Owns the XMPP connection for one Blazor circuit (one browser tab): login, online presence,
    /// and sending/receiving chat messages. Registered as scoped, so every tab gets its own
    /// connection, which is closed when the circuit ends.
    /// </summary>
    public sealed class XmppClientService : IAsyncDisposable
    {
        private readonly XmppOptions _options;
        private readonly ILogger<XmppClientService> _logger;
        private readonly BehaviorSubject<SessionState> _state = new(SessionState.Disconnected);
        private readonly Subject<ChatMessage> _messages = new();
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly CancellationTokenSource _disposeCts = new();

        // Client for the current login, or null when logged out. Each login gets a new client
        // so nothing (credentials, auto-reconnect state) carries over from a previous session.
        private XmppClient? _client;
        private IDisposable? _clientSubscriptions;
        private bool _disposed;

        public XmppClientService(IOptions<XmppOptions> options, ILogger<XmppClientService> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>
        /// Session state changes (Connected, Authenticating, Binded, Disconnected, ...).
        /// New subscribers get the current state right away. Raised on a background thread.
        /// </summary>
        public IObservable<SessionState> StateChanged => _state.DistinctUntilChanged();

        /// <summary>
        /// Chat messages sent to the logged-in user. Raised on a background thread.
        /// </summary>
        public IObservable<ChatMessage> MessageReceived => _messages.AsObservable();

        public SessionState State => _state.Value;

        /// <summary>
        /// True when logged in and able to send messages.
        /// </summary>
        public bool IsConnected => _client is not null && State == SessionState.Binded;

        /// <summary>
        /// Logs in and goes online. Completes once the session is ready and replaces any current session.
        /// </summary>
        /// <param name="jid">A full JID (budi@openfire) or just the username (budi).</param>
        /// <param name="password">The account password.</param>
        /// <param name="cancellationToken">Cancels the login attempt.</param>
        /// <exception cref="AuthenticationException">The username or password is wrong.</exception>
        public async Task ConnectAsync(string jid, string password, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(jid);
            ArgumentException.ThrowIfNullOrEmpty(password);
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Linked to _disposeCts so that closing the tab aborts a login still in progress.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);

            await _connectionLock.WaitAsync(cts.Token);
            try
            {
                await CloseCurrentClientAsync();

                var bareJid = ToBareJid(jid);
                var client = CreateClient(bareJid, password);
                var subscriptions = new CompositeDisposable(
                    client.StateChanged.Subscribe(state => OnStateChanged(client, state)),
                    client.XmppXElementReceived.OfType<Message>().Subscribe(OnMessageReceived));

                try
                {
                    // Returns once the session is authenticated and bound; throws if any step fails.
                    await client.ConnectAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "XMPP login failed for {Jid}.", bareJid);
                    await CloseClientAsync(client, subscriptions);
                    throw;
                }

                _client = client;
                _clientSubscriptions = subscriptions;
                _logger.LogInformation("Logged in to XMPP as {Jid}.", bareJid);
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        /// <summary>
        /// Logs out and closes the connection. Does nothing when not logged in.
        /// </summary>
        public async Task DisconnectAsync()
        {
            if (_disposed)
            {
                return;
            }

            await _connectionLock.WaitAsync();
            try
            {
                await CloseCurrentClientAsync();
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        /// <summary>
        /// Sends a chat message.
        /// </summary>
        /// <param name="to">The recipient's JID or username.</param>
        /// <param name="body">The message text.</param>
        /// <param name="cancellationToken">Cancels waiting for an earlier send to finish.</param>
        /// <exception cref="InvalidOperationException">Not logged in, or the connection is down.</exception>
        public async Task SendChatMessageAsync(string to, string body, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(to);
            ArgumentException.ThrowIfNullOrEmpty(body);

            var client = GetConnectedClient();
            try
            {
                await SendAsync(client, new Message(ToBareJid(to), MessageType.Chat, body), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send a chat message to {To}.", to);
                throw;
            }
        }

        /// <summary>
        /// Turns user input into a bare JID: "budi" becomes "budi@openfire" (the configured domain),
        /// and "budi@openfire/web" becomes "budi@openfire".
        /// </summary>
        public string ToBareJid(string userOrJid)
        {
            var value = userOrJid.Trim();

            var resourceStart = value.IndexOf('/');
            if (resourceStart >= 0)
            {
                value = value[..resourceStart];
            }

            return value.Contains('@') ? value : $"{value}@{_options.Domain}";
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            // Abort a login that is still in progress, then log out.
            _disposeCts.Cancel();
            await _connectionLock.WaitAsync();
            try
            {
                await CloseCurrentClientAsync();
            }
            finally
            {
                _connectionLock.Release();
            }

            _state.OnCompleted();
            _messages.OnCompleted();
            _disposeCts.Dispose();
        }

        private XmppClient CreateClient(string bareJid, string password)
        {
            return new XmppClient(conf =>
            {
                conf.UseWebSocketTransport(new StaticNameResolver(new Uri(_options.WebSocketUri)));
                conf.AutoReconnect = true;
            })
            {
                Jid = bareJid,
                Password = password,
            };
        }

        private XmppClient GetConnectedClient()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var client = _client;
            if (client is null || State != SessionState.Binded)
            {
                throw new InvalidOperationException("Not connected to the XMPP server.");
            }
            return client;
        }

        // ClientWebSocket allows only one send at a time and XmppDotNet doesn't queue them,
        // so every outgoing stanza goes through this lock.
        private async Task SendAsync(XmppClient client, XmppXElement stanza, CancellationToken cancellationToken = default)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await client.SendAsync(stanza);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private void OnStateChanged(XmppClient client, SessionState state)
        {
            _logger.LogDebug("XMPP session state: {State}", state);
            Publish(_state, state);

            // Runs after the first login and after every automatic reconnect.
            if (state == SessionState.Binded)
            {
                _ = AnnouncePresenceAsync(client);
            }
        }

        // A new XMPP session counts as offline until it sends presence, and the server won't
        // deliver messages addressed to the user's bare JID (budi@openfire) to it.
        private async Task AnnouncePresenceAsync(XmppClient client)
        {
            try
            {
                await SendAsync(client, new Presence(Show.Chat));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not send online presence.");
            }
        }

        private void OnMessageReceived(Message message)
        {
            // Skip stanzas without text (typing notifications, receipts) and bounced errors.
            if (string.IsNullOrEmpty(message.Body) || message.Type == MessageType.Error)
            {
                return;
            }

            var from = message.From?.Bare ?? string.Empty;
            Publish(_messages, new ChatMessage(from, message.Body, DateTimeOffset.Now));
        }

        // Subscriber code runs inside XmppDotNet's login and receive loops. An exception there
        // would fail the login or silently stop message delivery, so log it and carry on.
        private void Publish<T>(IObserver<T> observer, T value)
        {
            try
            {
                observer.OnNext(value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An XMPP event subscriber threw an exception.");
            }
        }

        private async Task CloseCurrentClientAsync()
        {
            var client = _client;
            var subscriptions = _clientSubscriptions;
            if (client is null || subscriptions is null)
            {
                return;
            }

            _client = null;
            _clientSubscriptions = null;
            await CloseClientAsync(client, subscriptions);
        }

        private async Task CloseClientAsync(XmppClient client, IDisposable subscriptions)
        {
            try
            {
                // Stop auto-reconnect first so it can't bring this session back.
                foreach (var handler in client.Handlers.OfType<ReconnectHandler>())
                {
                    await handler.Stop();
                }

                // Called even when already disconnected: the "Disconnecting" state it reports is
                // what tells XmppDotNet's reconnect handler not to start reconnecting again.
                await client.DisconnectAsync();
            }
            catch (Exception ex)
            {
                // Expected when the socket is already gone (never opened, or dropped before logout).
                _logger.LogDebug(ex, "Clean XMPP logout failed; closing the socket directly.");
                await client.Transport.DisconnectAsync();
            }
            finally
            {
                subscriptions.Dispose();
                Publish(_state, SessionState.Disconnected);

                // Stop() can't cancel a reconnect attempt that is already waiting out its delay, and
                // that attempt still opens a socket. Close any socket this client opens from now on.
                _ = client.Transport.StateChanged
                    .Where(state => state == XmppDotNet.Transport.State.Connected)
                    .Subscribe(_ => client.Transport.DisconnectAsync());
            }
        }
    }
}

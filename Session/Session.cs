using XmppDotNet.Xmpp.HttpUpload;

namespace chat.Session
{
    public class UserSession
    {
        private string _jid = string.Empty;
        public string Jid {
            get => _jid;
            set {
                if (_jid != value)
                {
                    _jid = value;
                    NotifyStateChanged();
                }
            }
        }
        public string Password { get; set; } = string.Empty;

        public event Action? OnChange;
        public void NotifyStateChanged() => OnChange?.Invoke();
    }
}

namespace chat.Services
{
    /// <summary>
    /// XMPP server settings, read from the "Xmpp" section of appsettings.json.
    /// </summary>
    public class XmppOptions
    {
        public const string SectionName = "Xmpp";

        /// <summary>
        /// WebSocket endpoint of the XMPP server, e.g. ws://localhost:7070/ws/ for Openfire.
        /// </summary>
        public string WebSocketUri { get; set; } = string.Empty;

        /// <summary>
        /// XMPP domain added to plain usernames, so "budi" becomes "budi@openfire".
        /// </summary>
        public string Domain { get; set; } = string.Empty;
    }
}

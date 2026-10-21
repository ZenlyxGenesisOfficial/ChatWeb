namespace chat.Services
{
    /// <summary>
    /// A chat message received from another user.
    /// </summary>
    /// <param name="From">Bare JID of the sender, e.g. budi@openfire.</param>
    /// <param name="Body">The message text.</param>
    /// <param name="Timestamp">When the message arrived.</param>
    public record ChatMessage(string From, string Body, DateTimeOffset Timestamp);
}

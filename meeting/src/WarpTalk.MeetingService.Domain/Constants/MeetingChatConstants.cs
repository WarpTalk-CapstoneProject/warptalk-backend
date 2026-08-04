namespace WarpTalk.MeetingService.Domain.Constants;

public static class MeetingChatConstants
{
    /// <summary>
    /// Longest chat message the meeting room accepts (WT-237).
    ///
    /// Mirrored by MAX_CHAT_MESSAGE_LENGTH in the web client, which stops the typing at this
    /// count. The check here is what actually holds — the column is TEXT, so nothing below
    /// this layer bounds the message, and the desktop app posts to the same endpoint.
    /// </summary>
    public const int MaxMessageLength = 1000;
}

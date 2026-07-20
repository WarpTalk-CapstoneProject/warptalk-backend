namespace WarpTalk.TranslationRoomService.Domain.Constants;

public static class TranslationRoomSessionConstants
{
    // Error Messages
    public const string ErrorSessionNotFound = "Session not found.";
    public const string ErrorSessionNotBelongToRoom = "Session does not belong to the specified room.";
    public const string ErrorCannotUpdateEndedSession = "Cannot update an ENDED session.";
    public const string ErrorUnexpected = "Unexpected error occurred.";
}

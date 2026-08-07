namespace WarpTalk.TranslationRoomService.Domain.Constants;

public static class TranslationRoomSessionConstants
{
    // Error Messages
    public const string ErrorSessionNotFound = "Session not found.";
    public const string ErrorSessionNotBelongToRoom = "Session does not belong to the specified room.";
    public const string ErrorCannotUpdateEndedSession = "Cannot update an ENDED session.";
    public const string ErrorUnexpected = "Unexpected error occurred.";

    /// <summary>
    /// Wording kept in sync with <see cref="TranslationRoomConstants.ErrorUnauthorizedAdmitParticipant"/>:
    /// both guard the same "room host OR workspace Owner/Admin" predicate, so a user who is refused
    /// one and allowed the other should not be told two different stories about why.
    /// </summary>
    public const string ErrorUnauthorizedManageSession = "Only the host or a workspace owner/admin can manage translation sessions.";

    public const string ErrorUnauthorizedViewSessions = "Unauthorized. Only the room host, a participant of this room, or an invited user can view its translation sessions.";
}

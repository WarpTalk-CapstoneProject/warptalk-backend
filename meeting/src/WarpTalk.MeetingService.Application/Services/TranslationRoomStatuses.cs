namespace WarpTalk.MeetingService.Application.Services;

/// <summary>Statuses of a translation room (as GetTranslationRoomResponse.Status carries them).</summary>
public static class TranslationRoomStatuses
{
    /// <summary>
    /// A room nobody may join or rejoin any more. EXPIRED belongs here (WT-714): a booking nobody
    /// attended is moved there by the booking sweep. Exact, case-sensitive match — the stored values
    /// are upper case, and this is the comparison the join and bridge-token gates always made.
    /// </summary>
    public static bool IsEnded(string? status) =>
        status is "ENDED" or "FINISHED" or "CANCELLED" or "EXPIRED";
}

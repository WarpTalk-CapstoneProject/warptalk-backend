namespace WarpTalk.MeetingService.Application.Services;

/// <summary>Statuses of a translation room (as GetTranslationRoomResponse.Status carries them).</summary>
public static class TranslationRoomStatuses
{
    /// <summary>
    /// A room nobody may join or rejoin any more. EXPIRED belongs here (WT-714): a booking nobody
    /// attended is moved there by the booking sweep.
    ///
    /// Case-insensitive. translation-room only ever stores these in upper case, so for the join and
    /// bridge-token gates (which compared exactly) nothing observable changes, and the only thing a
    /// wider match could do there is refuse a join into a room that is over — the safe direction.
    /// The bridge recording auto-stop always matched without case.
    /// </summary>
    public static bool IsEnded(string? status) =>
        !string.IsNullOrWhiteSpace(status) && EndedStatuses.Contains(status);

    private static readonly HashSet<string> EndedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ENDED", "FINISHED", "CANCELLED", "EXPIRED",
    };
}

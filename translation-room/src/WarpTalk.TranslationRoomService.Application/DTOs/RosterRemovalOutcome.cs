namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// WT-699: what a kick or a lobby reject did to the roster, for a caller that has to tell the
/// host something true.
///
/// All three are successes — the state the host asked for holds in each — but they are different
/// sentences. TC2103 was the host pressing Kick a second time and being told "kicked" again for a
/// person who had been gone for minutes; <see cref="AlreadyRemoved"/> is what lets the answer say
/// so instead.
/// </summary>
public enum RosterRemovalOutcome
{
    /// <summary>The person had no roster row here. Nothing to terminate; not an error.</summary>
    NotOnRoster,

    /// <summary>This call wrote the terminal status.</summary>
    Removed,

    /// <summary>The row already carried that terminal status before this call. Nothing written.</summary>
    AlreadyRemoved,
}

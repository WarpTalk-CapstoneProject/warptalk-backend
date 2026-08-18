using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// Asks AuthService whether a person has agreed to have their voice cloned.
///
/// This service does not store that answer. Consent to biometric processing belongs to the
/// person, not to a meeting, and the record lives beside the voice profiles it authorises in
/// AuthService's own database. Keeping a copy here would be a second place for the answer to
/// live, and a permission with two homes eventually has two answers.
/// </summary>
public interface IVoiceConsentDirectory
{
    /// <summary>
    /// True only for a live grant. Implementations must return FALSE when AuthService cannot be
    /// reached: this stands in front of biometric processing, and a gate that opens when it is
    /// unsure is not a gate.
    /// </summary>
    Task<bool> HasVoiceCloneConsentAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>
/// WT-396: the voice a speaker chose to be DUBBED IN, asked of AuthService.
///
/// Separate from IVoiceConsentDirectory above because they answer different questions about the
/// same person — that one is "may we clone them at all", this one is "and which voice did they
/// ask for". A single directory returning both would make it easy to check one and act on the
/// other, which is close enough to the bug being fixed to be worth the extra interface.
/// </summary>
public interface IDubVoiceDirectory
{
    /// <summary>
    /// Everything AuthService knows about how this speaker should be voiced, in ONE call.
    ///
    /// One call rather than one per fact, deliberately. The mesh is O(n^2) in participants and
    /// this runs on the path that starts every meeting; asking twice per speaker would double
    /// the round trips on the exact call site whose comment already explains why it asks once.
    /// AuthService answers both from the same row read anyway.
    ///
    /// Never throws. Every field null is the honest answer for a guest, for somebody who has
    /// chosen nothing, and for an AuthService that cannot be reached — and all three mean the
    /// same thing to the workers: clone them live, which is what everybody had before any of
    /// this existed.
    /// </summary>
    Task<DubVoiceSelection> GetSelectionAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>How one speaker should be voiced.</summary>
/// <param name="ChosenVoiceId">
/// WT-396 — a voice they DELIBERATELY PICKED. The worker must stop capturing and never overwrite
/// it.
/// </param>
/// <param name="AutoCloneVoiceId">
/// WT-B — a voice captured from them in an earlier meeting. The opposite instruction: a starting
/// point the worker is supposed to keep improving on.
///
/// Kept apart from <paramref name="ChosenVoiceId"/> for exactly that reason. Collapsed into one
/// field, a carried clone would be read as a pick, capture would stop, and every speaker would be
/// frozen at the first clone they ever earned.
/// </param>
/// <param name="AutoCloneScore">
/// How good the clip behind the carried voice was — the bar a later clip must beat.
///
/// A string, and null means NOT MEASURED rather than zero: zero grades as the worst possible
/// sample and would invite replacement by anything at all. Carried as the text AuthService
/// formatted and never re-parsed, because a decimal round-tripped through a comma-decimal locale
/// is how 0.006575 became 6575 in billing.
/// </param>
public sealed record DubVoiceSelection(
    string? ChosenVoiceId,
    string? AutoCloneVoiceId,
    string? AutoCloneScore)
{
    /// <summary>A speaker nothing is known about — a guest, or an unreachable AuthService.</summary>
    public static readonly DubVoiceSelection None = new(null, null, null);
}

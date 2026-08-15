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
    /// The provider voice id, or null when the speaker has not chosen one, is a guest, or
    /// AuthService cannot answer. Null always means "clone them live from the meeting instead",
    /// which is what happened for everybody before this existed — so an outage degrades to the
    /// previous behaviour rather than to silence.
    /// </summary>
    Task<string?> GetDubVoiceAsync(Guid userId, CancellationToken ct = default);
}

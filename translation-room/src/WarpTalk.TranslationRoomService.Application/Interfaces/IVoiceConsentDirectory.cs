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

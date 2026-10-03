using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// WT-933. The host records which Meet-side people agreed to have their voice cloned.
///
/// WHY THE HOST, AND NOT THE PERSON
///     The people on the Meet side are not WarpTalk users. They have no account, no session and
///     no screen of ours to tick anything on — in this service they are all one stand-in
///     participant. So the host asks them in the call, and records the answer here on their
///     behalf (product decision, 2026-10-03). That is also why both operations are host-only:
///     the host is the one person accountable for having asked.
///
/// WHERE IT GOES
///     One Redis hash per room, read directly by the TTS worker. There is no database row and no
///     event: the worker looks the field up when it is about to clone, and deletes the voice
///     model when the field is gone. A field that is present IS the consent.
///
/// WHAT IT NEVER KEEPS
///     The display name. Only a SHA-256 of the folded name is written, and only the first 12 hex
///     chars of that are logged. See <see cref="Helpers.FarSpeakerCloneConsent"/>.
/// </summary>
public interface IBridgeVoiceCloneConsentService
{
    /// <summary>
    /// Record (<paramref name="consented"/> true) or withdraw (false) the consent of the Meet-side
    /// person shown as <paramref name="displayName"/>. Idempotent both ways. HOST ONLY, in an
    /// EXTERNAL_BRIDGE room that has not ended.
    /// </summary>
    Task<Result<BridgeVoiceCloneConsentDto>> SetAsync(
        Guid roomId, Guid userId, string? displayName, bool consented, CancellationToken ct = default);

    /// <summary>
    /// Which of <paramref name="displayNames"/> have a consent in force — returned exactly as
    /// submitted. Same gate as <see cref="SetAsync"/>: who agreed is the host's to know.
    /// </summary>
    Task<Result<BridgeVoiceCloneConsentStatusDto>> GetStatusAsync(
        Guid roomId, Guid userId, IReadOnlyList<string?>? displayNames, CancellationToken ct = default);
}

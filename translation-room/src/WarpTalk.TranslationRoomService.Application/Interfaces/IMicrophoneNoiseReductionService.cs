using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// How much denoising the STT provider applies to ONE PARTICIPANT'S microphone in one meeting.
///
/// WHY THIS EXISTS AT ALL
///     WT-427 built the read half of a per-ROOM denoising mode in stt_worker and stopped there.
///     Nothing in this repo, in warptalk-web, or in any script ever wrote the key, so the feature
///     has been inert since it shipped — which is why the report it was opened for ("chỉ bắt voice
///     ở gần mic thì transcript khá chính xác, nới ra thì transcript tệ hẳn") was never answered.
///
/// WHY THE PARTICIPANT AND NOT THE ROOM
///     Denoising describes a MICROPHONE, not a meeting. The case that needs it most is the mixed
///     one — a headset and a laptop two metres from a fan, in the same call — and a single
///     room-wide value is wrong for one of those two whichever way it is set. The transcription
///     session on the AI side is already keyed per (meeting, speaker), so the room was never the
///     finest grain available.
///
///     The room key still exists and the AI side still reads it as the fallback. It is an
///     operator's escape hatch (redis-cli during a meeting nobody can hear), not a second product
///     surface, and it is deliberately not exposed here.
///
/// WHY IT IS NOT HOST-GATED — READ BEFORE "FIXING" THAT
///     IRoomFlashModeService next door IS host-only, and the difference is the whole design.
///     Flash mode changes how EVERYBODY in the room is transcribed. This changes how ONE person's
///     own microphone is handled, and affects no other participant's audio at all. Gating it on
///     the host would mean a guest in a noisy room has to ask permission to be understood, which
///     is the failure mode this feature exists to remove. Self-service, like voice-clone consent.
///
/// The key is optional on the AI side: a participant who never sets it falls back to the room key
/// and then to the deployment default, so this service failing leaves a meeting behaving exactly
/// as it did before any of this existed.
/// </summary>
public interface IMicrophoneNoiseReductionService
{
    /// <summary>
    /// This caller's own mode for this meeting, or "off" when they have never chosen one.
    ///
    /// "off" rather than null for the unset case, because "off" is what the pipeline will actually
    /// do for them — the deployment default — and a UI showing "unset" would be describing a
    /// distinction the audio does not have.
    /// </summary>
    Task<Result<string>> GetAsync(Guid roomId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Set this caller's own mode for this meeting. Any participant, for themselves only.
    ///
    /// The caller's id is the one written — there is deliberately no "for this other participant"
    /// parameter, so the endpoint cannot be used to reconfigure somebody else's microphone.
    /// </summary>
    Task<Result<string>> SetAsync(Guid roomId, Guid userId, string mode, CancellationToken ct = default);

    /// <summary>
    /// Record what the CLIENT's own denoiser ended up doing. An observation, not a setting.
    ///
    /// This is about a DIFFERENT denoiser from the two methods above it, and they are deliberately
    /// on the same service because from a participant's point of view they are one question: is
    /// anything cleaning up my microphone. Above is the provider-side pass at the STT session;
    /// this is Krisp, in the browser, before the audio is ever published.
    ///
    /// It exists because that second one fails SILENTLY. Attaching the processor succeeds;
    /// enabling it asks the LiveKit project whether it is entitled, and livekit-client calls that
    /// path un-awaited and un-caught, so a denial rejects nothing and the toggle sits there lit
    /// over a filter that is not running. The web client checks by hand and reports the answer
    /// here, so the question "is noise suppression actually working in production" has an answer
    /// in the service log instead of only in one participant's browser console.
    ///
    /// Nothing downstream reads what this writes. It is diagnostics, and a failure to record it
    /// must never be allowed to affect the meeting — see the implementation.
    /// </summary>
    Task<Result<bool>> ReportClientSuppressionAsync(
        Guid roomId, Guid userId, ReportNoiseSuppressionDto report, CancellationToken ct = default);
}

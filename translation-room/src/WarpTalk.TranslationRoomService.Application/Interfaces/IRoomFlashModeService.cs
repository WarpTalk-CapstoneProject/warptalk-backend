using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// Flash mode — whether this room streams a speaker's audio to STT WHILE they are still talking,
/// instead of waiting for VAD to close the turn.
///
/// WHY IT IS A ROOM SETTING AND NOT A USER ONE
///     One Redis key per room is what the ingress worker reads, and it governs how audio is
///     turned into text for everybody in that room. There is no per-person version of it to
///     offer: a participant cannot stream their own audio differently from the room they are in.
///     That is also why it is gated on the host — see SetAsync.
///
/// WHY IT IS NOT AN AUDIO ROUTE
///     Routes are the who-hears-whom mesh. This is a property of the transcription pipeline, and
///     folding it into the route payload would put a deployment-shaped switch inside a structure
///     that is rebuilt and rebroadcast on every join, leave and language change.
///
/// The key is optional on the AI side: a room that never sets it uses the deployment default, so
/// this service failing is a room that behaves exactly as it did before flash mode existed.
/// </summary>
public interface IRoomFlashModeService
{
    /// <summary>
    /// Whether this room is streaming audio during speech RIGHT NOW, and where that answer came
    /// from — a host's override, the deployment default, or neither.
    ///
    /// It returned a bare bool meaning "an override exists and says on", which is a different
    /// question from the one the switch is asking. While the deployment defaulted to off the two
    /// agreed; once it defaulted to on, every untouched room reported "off" while streaming.
    /// </summary>
    Task<Result<FlashModeStateDto>> GetAsync(Guid roomId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Turn flash mode on or off for the whole room. HOST ONLY.
    ///
    /// Not self-service, unlike voice-clone consent and the dub-voice refresh beside it, and the
    /// difference is real: those change how ONE person is heard, this changes how everybody in
    /// the room is transcribed. A participant flipping it would be reconfiguring the pipeline
    /// underneath five other people who never asked.
    /// </summary>
    Task<Result<bool>> SetAsync(Guid roomId, Guid userId, bool enabled, CancellationToken ct = default);
}

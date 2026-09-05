using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using GetTranslationRoomRequest = WarpTalk.Shared.Protos.GetTranslationRoomRequest;
using TranslationRoomServiceClient = WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient;

namespace WarpTalk.TranscriptService.Application.Authorization;

/// <summary>
/// WT-605. Who may Pause/Resume Transcript: the room's host, and only the host.
///
/// Deliberately host-only rather than the host-OR-participant scope <see cref="TranscriptReadAccess"/>
/// grants for reading — Pause/Resume Transcript is a room-wide write with no undo for the window
/// it skips, same tier as Pause Room / Stop Translation on the translation-room side (both also
/// host-only). A separate check rather than reusing ITranscriptReadAccess so widening read access
/// later can never silently widen who can stop recording.
/// </summary>
public interface ITranscriptPauseAccess
{
    /// <summary>True when <paramref name="userId"/> is the room's host. A room that no longer
    /// exists returns false rather than throwing, so callers surface NotFound instead of a 500.</summary>
    Task<bool> IsRoomHostAsync(Guid translationRoomId, Guid userId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ITranscriptPauseAccess"/>
public sealed class TranscriptPauseAccess : ITranscriptPauseAccess
{
    private readonly TranslationRoomServiceClient _roomClient;

    public TranscriptPauseAccess(TranslationRoomServiceClient roomClient)
    {
        _roomClient = roomClient;
    }

    public async Task<bool> IsRoomHostAsync(
        Guid translationRoomId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var room = await _roomClient.GetTranslationRoomByIdAsync(
                new GetTranslationRoomRequest { Id = translationRoomId.ToString() },
                cancellationToken: cancellationToken);

            return Guid.TryParse(room.HostId, out var hostId) && hostId == userId;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return false;
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using WarpTalk.Shared;
using GetTranslationRoomRequest = WarpTalk.Shared.Protos.GetTranslationRoomRequest;
using TranslationRoomServiceClient = WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient;

namespace WarpTalk.TranscriptService.Application.Authorization;

/// <summary>
/// WT-605. Who may Pause/Resume Transcript: the room's host — and, in an EXTERNAL_BRIDGE room
/// only, the bridge's current capturer (PO rule 2026-10-01,
/// <see cref="ExternalBridgeConstants.CanControlBridgeSession"/>).
///
/// Deliberately narrower than the host-OR-participant scope <see cref="TranscriptReadAccess"/>
/// grants for reading — Pause/Resume Transcript is a room-wide write with no undo for the window
/// it skips, same tier as Pause Room / Stop Translation on the translation-room side. A separate
/// check rather than reusing ITranscriptReadAccess so widening read access later can never
/// silently widen who can stop recording.
///
/// "Host" stays what this gate has always compared against — <c>HostId</c>, the booker — so a
/// non-bridge room answers exactly as before; the capturer clause is the only addition.
/// </summary>
public interface ITranscriptPauseAccess
{
    /// <summary>True when <paramref name="userId"/> may pause/resume this room's transcript. A room
    /// that no longer exists returns false rather than throwing, so callers surface a refusal
    /// instead of a 500.</summary>
    Task<bool> CanPauseTranscriptAsync(Guid translationRoomId, Guid userId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ITranscriptPauseAccess"/>
public sealed class TranscriptPauseAccess : ITranscriptPauseAccess
{
    private readonly TranslationRoomServiceClient _roomClient;

    public TranscriptPauseAccess(TranslationRoomServiceClient roomClient)
    {
        _roomClient = roomClient;
    }

    public async Task<bool> CanPauseTranscriptAsync(
        Guid translationRoomId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var room = await _roomClient.GetTranslationRoomByIdAsync(
                new GetTranslationRoomRequest { Id = translationRoomId.ToString() },
                cancellationToken: cancellationToken);

            if (Guid.TryParse(room.HostId, out var hostId) && hostId == userId)
                return true;

            // Host already answered above with the same Guid comparison as before. effectiveHostId
            // is deliberately not passed: this gate never honoured a transfer, and this change must
            // not widen it for non-bridge rooms — only the bridge capturer clause can still say yes.
            return ExternalBridgeConstants.CanControlBridgeSession(
                room.TranslationRoomType,
                hostId: null,
                effectiveHostId: null,
                room.BridgeCapturerUserId,
                userId.ToString());
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return false;
        }
    }
}

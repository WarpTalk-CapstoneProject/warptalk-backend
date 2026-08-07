using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// Room lookups made by other services over gRPC. Separate from
/// <see cref="ITranslationRoomParticipantService"/>, whose operations all take a
/// requesting user id and enforce that user's permissions — a server-to-server
/// caller has no such user to check against.
/// </summary>
public interface ITranslationRoomDirectoryService
{
    /// <summary>
    /// WT-334: the room detail read WITHOUT a user check — the mesh's copy of what
    /// <see cref="ITranslationRoomService.GetTranslationRoomAsync"/> used to be for everyone.
    ///
    /// It lives here rather than as an optional <c>userId</c> on the user-facing interface because
    /// this interface is the codebase's existing statement of "server-to-server caller, no user to
    /// check against" — the same reason <see cref="GetParticipantsAsync"/> is here and not on
    /// <see cref="ITranslationRoomParticipantService"/>. That makes the exemption a property of the
    /// TYPE: this interface is resolved only by <c>TranslationRoomGrpcService</c> and has no HTTP
    /// surface, so a future controller cannot reach the unchecked read by passing a null. A
    /// nullable "skip the check" parameter would leave both callers on one signature and make the
    /// next unauthenticated read a one-word mistake.
    ///
    /// Reached only over the internal gRPC port. WorkspaceService is the caller of record
    /// (<c>TranslationRoomGrpcClient</c> → <c>DocumentAccessEvaluator</c>,
    /// <c>WorkspaceDocumentService</c>), and it applies its own document-level authorization to the
    /// answer.
    /// </summary>
    Task<Result<TranslationRoomDto>> GetRoomAsync(
        Guid translationRoomId,
        CancellationToken ct = default);

    Task<Result<IReadOnlyList<TranslationRoomParticipantSummaryDto>>> GetParticipantsAsync(
        Guid translationRoomId,
        CancellationToken ct = default);

    Task<Result<int>> CountActiveRoomsByWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default);
}

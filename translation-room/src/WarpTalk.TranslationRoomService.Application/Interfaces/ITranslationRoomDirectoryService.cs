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
    Task<Result<IReadOnlyList<TranslationRoomParticipantSummaryDto>>> GetParticipantsAsync(
        Guid translationRoomId,
        CancellationToken ct = default);

    Task<Result<int>> CountActiveRoomsByWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default);
}

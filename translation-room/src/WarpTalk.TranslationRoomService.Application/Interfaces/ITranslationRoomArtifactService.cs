using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public interface ITranslationRoomArtifactService
{
    Task<Result<List<RoomArtifactDto>>> GetRoomArtifactsAsync(Guid roomId, Guid userId, CancellationToken ct = default);
    Task<Result<string>> GetArtifactDownloadUrlAsync(Guid artifactId, Guid userId, CancellationToken ct = default);
    Task<Result> ApproveArtifactConsentAsync(Guid artifactId, Guid userId, CancellationToken ct = default);
}

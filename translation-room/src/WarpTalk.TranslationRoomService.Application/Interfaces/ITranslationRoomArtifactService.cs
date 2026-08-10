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
    Task<Result<ArtifactDownloadDto>> GetArtifactDownloadAsync(Guid artifactId, Guid userId, CancellationToken ct = default);
    Task<Result> ApproveArtifactConsentAsync(Guid artifactId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Asks the AI assistant to write this meeting's summary again in a different shape.
    ///
    /// The default summary is General and is written once when the meeting ends. This is the
    /// second look — deciding a finished meeting was really a standup or an interview is a
    /// judgement nobody can make before it has happened, so the choice belongs here rather
    /// than in the create-meeting form.
    /// </summary>
    Task<Result> RegenerateSummaryAsync(
        Guid roomId,
        Guid userId,
        string templateKey,
        string? bearerToken,
        CancellationToken ct = default);
}

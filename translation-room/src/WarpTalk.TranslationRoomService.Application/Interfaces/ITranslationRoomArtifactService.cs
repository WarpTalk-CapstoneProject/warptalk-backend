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
    /// Asks the AI assistant to write this meeting's summary again — in a different shape, a
    /// different language, or both.
    ///
    /// The default summary is General and is written once when the meeting ends. This is the
    /// second look — deciding a finished meeting was really a standup or an interview is a
    /// judgement nobody can make before it has happened, so the choice belongs here rather
    /// than in the create-meeting form.
    ///
    /// <paramref name="summaryLanguage"/> is the same kind of after-the-fact decision. The
    /// language a summary came out in used to be the model's, inferred from the transcript, so
    /// a room whose members read different languages had no way to ask for anything else.
    /// Null or empty means the caller expressed no preference and the model follows the
    /// transcript, which is what every summary already in storage was written under.
    /// </summary>
    Task<Result> RegenerateSummaryAsync(
        Guid roomId,
        Guid userId,
        string templateKey,
        string? summaryLanguage,
        string? bearerToken,
        CancellationToken ct = default);
}

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
    /// <summary>
    /// This meeting's summary in the shape and language the READER asked for, generating it if
    /// nobody has asked for that pair yet.
    ///
    /// WHY THIS IS NOT RegenerateSummaryAsync WITH A LANGUAGE.
    ///     Regenerating REPLACES the room's one summary artifact, and its gate admits every
    ///     participant. So a Japanese attendee choosing Japanese destroyed the English summary
    ///     the host had published — for everybody, until the host pressed the button again. Two
    ///     people who read different languages could take that from each other indefinitely.
    ///
    ///     Reading is not writing. This method never touches the canonical artifact: the pair
    ///     the host published is served from it, and every other pair is served from a cache
    ///     beside it that the reader's request fills in.
    ///
    /// The first caller for a pair gets <c>generating</c> and refetches; everyone after them
    /// gets the content immediately. That is the whole of "select mới gen" — nothing is
    /// generated before somebody asks, and nothing is generated twice.
    /// </summary>
    Task<Result<SummaryVariantDto>> GetOrQueueSummaryVariantAsync(
        Guid roomId,
        Guid userId,
        string templateKey,
        string? language,
        string? bearerToken,
        CancellationToken ct = default);

    /// <summary>
    /// Which renderings this room already holds, so the picker can show which choices are
    /// instant and which will cost a wait rather than making every one look free.
    /// </summary>
    Task<Result<List<SummaryVariantSummaryDto>>> GetSummaryVariantsAsync(
        Guid roomId,
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Queue a rewrite, and hand back the id it was queued under.
    ///
    /// The id is not decoration: it is how the requester finds out what happened. A rewrite is
    /// answered asynchronously and its failures used to reach a log and stop, so the caller is
    /// given something to ask about — see <see cref="GetSummaryRewriteStatusAsync"/>.
    /// </summary>
    Task<Result<string>> RegenerateSummaryAsync(
        Guid roomId,
        Guid userId,
        string templateKey,
        string? summaryLanguage,
        string? bearerToken,
        CancellationToken ct = default);

    /// <summary>
    /// What became of one queued rewrite.
    ///
    /// Behind the same access gate as reading the room's artifacts, and for the same reason: the
    /// failure text quotes what the worker found in this meeting's transcript.
    /// </summary>
    Task<Result<SummaryRewriteStatusDto>> GetSummaryRewriteStatusAsync(
        Guid roomId,
        Guid userId,
        string requestId,
        CancellationToken ct = default);
}

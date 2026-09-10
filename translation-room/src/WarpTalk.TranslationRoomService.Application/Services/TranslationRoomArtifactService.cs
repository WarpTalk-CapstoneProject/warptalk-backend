using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Domain.ValueObjects;

namespace WarpTalk.TranslationRoomService.Application.Services;

public class TranslationRoomArtifactService : ITranslationRoomArtifactService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<TranslationRoomArtifactService> _logger;
    private readonly IArtifactUrlSigner _urlSigner;
    private readonly IRedisStateRepository _redisStateRepo;
    private readonly IArtifactsFinalizationQueue _finalizationQueue;

    // Moved to TranslationRoomConstants: ArtifactsFinalizer publishes to this stream too, and
    // two private copies of a stream name is how one of them ends up renamed alone.
    private const string SummaryRequestStream = TranslationRoomConstants.SummaryRequestStream;

    public TranslationRoomArtifactService(
        IUnitOfWork unitOfWork,
        ILogger<TranslationRoomArtifactService> logger,
        IArtifactUrlSigner urlSigner,
        IRedisStateRepository redisStateRepo,
        IArtifactsFinalizationQueue finalizationQueue)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _urlSigner = urlSigner;
        _redisStateRepo = redisStateRepo;
        _finalizationQueue = finalizationQueue;
    }

    public async Task<Result> RegenerateSummaryAsync(
        Guid roomId,
        Guid userId,
        string templateKey,
        string? summaryLanguage,
        string? bearerToken,
        CancellationToken ct = default)
    {
        try
        {
            var room = await _unitOfWork.TranslationRoomRepository.FirstOrDefaultAsync(
                r => r.Id == roomId,
                "TranslationRoomParticipants,TranslationRoomArtifacts",
                ct);

            if (room == null)
                return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            // Same two gates as reading the artifacts, for the same reasons: there is nothing
            // to summarise until the meeting is over, and re-summarising exposes the whole
            // transcript to whoever asks.
            if (!TranslationRoomConstants.TerminalStatuses.Contains(room.Status.ToString()))
                return Result.Failure("A summary can only be rewritten for a finished meeting.", ErrorCodes.InvalidState);

            if (!ArtifactAccessHelper.HasAccessToRoomArtifacts(room, userId))
                return Result.Failure("Unauthorized to summarise this room.", ErrorCodes.Unauthorized);

            // A REWRITE NEEDS SOMETHING TO REWRITE.
            //
            // SummaryResultConsumerWorker REPLACES the room's SUMMARY_EXPORT and deliberately
            // refuses to invent one — "inventing one here would create an artifact the finalizer
            // never made and whose other columns nobody set". This method never checked, so on a
            // meeting that was never finalized the button returned success, the worker read the
            // transcript, spent an LLM call, published a perfectly good summary, and the consumer
            // logged "No summary artifact to rewrite" and dropped it. Every press. Which is
            // exactly the meeting a person is most likely to press it on: the one showing no
            // summary at all.
            //
            // Finalization, not a request, is what that meeting is missing — so ask for the thing
            // it actually needs. Queued only when the meeting has NEITHER text artifact, which is
            // precisely what ArtifactsFinalizer.FinalizeRoomArtifactsAsync writes (both, in one
            // save, or neither); with a transcript already stored, re-finalizing would add a
            // SECOND one rather than fill the gap, and that is a repair for a human to choose.
            var storedArtifacts = room.TranslationRoomArtifacts
                .Where(artifact => artifact.DeletedAt == null)
                .ToList();
            var hasSummaryArtifact = storedArtifacts.Any(artifact =>
                string.Equals(artifact.ArtifactType, ArtifactType.SUMMARY_EXPORT.ToString(), StringComparison.OrdinalIgnoreCase));

            if (!hasSummaryArtifact)
            {
                var hasTranscriptArtifact = storedArtifacts.Any(artifact =>
                    string.Equals(artifact.ArtifactType, ArtifactType.TRANSCRIPT_EXPORT.ToString(), StringComparison.OrdinalIgnoreCase));

                if (hasTranscriptArtifact)
                {
                    return Result.Failure(
                        "This meeting has a transcript but no summary artifact to rewrite. It needs to be finalized again, not re-summarised.",
                        ErrorCodes.InvalidState);
                }

                _logger.LogInformation(
                    "Room {RoomId} has no artifacts at all; queueing finalization instead of a summary rewrite that would have nothing to land on.",
                    roomId);
                _finalizationQueue.QueueFinalization(roomId);
                return Result.Success();
            }

            var targetLanguages = LanguageHelper.ParseTargetLanguages(room.TargetLanguages);

            await _redisStateRepo.StreamAddAsync(SummaryRequestStream, new Dictionary<string, string>
            {
                ["request_id"] = Guid.NewGuid().ToString(),
                ["room_id"] = roomId.ToString(),
                ["workspace_id"] = room.WorkspaceId.ToString(),
                ["template_key"] = string.IsNullOrWhiteSpace(templateKey) ? "general" : templateKey.Trim().ToLowerInvariant(),
                // Forwarded so the worker reads the transcript AS THE CALLER, through the
                // same authenticated endpoint they could already use — never a privileged
                // bypass that would let a regeneration read more than its requester can.
                ["bearer_token"] = bearerToken ?? string.Empty,
                ["target_languages_json"] = JsonSerializer.Serialize(targetLanguages),
                // Normalised to a bare ISO 639-1 code, matching what the AI side keys its
                // language names by: a room stores `vi-VN` and a picker sends `vi`, and a
                // summary must not come out in a different language depending on which
                // spelling reached it. Empty means the caller expressed no preference.
                ["summary_language"] = LanguageHelper.NormalizeLanguageCode(summaryLanguage),
                ["timestamp_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
            });

            _logger.LogInformation(
                "Queued summary regeneration for room {RoomId} with template {TemplateKey} in language {SummaryLanguage}",
                roomId,
                templateKey,
                LanguageHelper.NormalizeLanguageCode(summaryLanguage) is { Length: > 0 } code ? code : "as-spoken");

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue summary regeneration for room {RoomId}", roomId);
            return Result.Failure("Could not queue the summary rewrite.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<List<RoomArtifactDto>>> GetRoomArtifactsAsync(Guid roomId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var room = await _unitOfWork.TranslationRoomRepository.FirstOrDefaultAsync(
                r => r.Id == roomId,
                "TranslationRoomParticipants,TranslationRoomArtifacts",
                ct);

            if (room == null) return Result.Failure<List<RoomArtifactDto>>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            if (!TranslationRoomConstants.TerminalStatuses.Contains(room.Status.ToString()))
            {
                return Result.Failure<List<RoomArtifactDto>>("Artifacts are only available for finished rooms.", ErrorCodes.InvalidState);
            }

            if (!ArtifactAccessHelper.HasAccessToRoomArtifacts(room, userId))
                return Result.Failure<List<RoomArtifactDto>>("Unauthorized to view artifacts for this room.", ErrorCodes.Unauthorized);

            var artifacts = await _unitOfWork.TranslationRoomArtifactRepository.GetArtifactsByRoomIdAsync(roomId, ct);
            var dtos = artifacts?.Select(a => a.ToDto()).ToList() ?? new List<RoomArtifactDto>();
            return Result<List<RoomArtifactDto>>.Success(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting artifacts for room {RoomId}", roomId);
            return Result.Failure<List<RoomArtifactDto>>("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<ArtifactDownloadDto>> GetArtifactDownloadAsync(Guid artifactId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var artifact = await _unitOfWork.TranslationRoomArtifactRepository.GetArtifactWithRoomAsync(artifactId, ct);

            if (artifact == null) return Result.Failure<ArtifactDownloadDto>("Artifact not found.", ErrorCodes.NotFound);

            if (!ArtifactAccessHelper.HasAccessToRoomArtifacts(artifact.TranslationRoom, userId))
            {
                // Named rather than flat — see ArtifactAccessHelper.DescribeArtifactDenial. The
                // participant roster is already loaded on the room this query returned, so saying
                // WHICH refusal this is costs nothing beyond the predicate.
                var wasThere = artifact.TranslationRoom.TranslationRoomParticipants
                    .Any(participant => participant.UserId == userId);
                return Result.Failure<ArtifactDownloadDto>(
                    ArtifactAccessHelper.DescribeArtifactDenial(wasThere),
                    ErrorCodes.Unauthorized);
            }

            if (artifact.RetentionUntil.HasValue && DateTime.UtcNow > artifact.RetentionUntil.Value)
            {
                artifact.Status = ArtifactStatus.Expired.ToString();
                _unitOfWork.TranslationRoomArtifactRepository.Update(artifact);
                await _unitOfWork.SaveChangesAsync(ct);
                return Result.Failure<ArtifactDownloadDto>("Artifact retention period has expired.", ErrorCodes.InvalidState);
            }

            if (artifact.ConsentRequired)
            {
                return Result.Failure<ArtifactDownloadDto>("Consent is required before downloading this artifact.", ErrorCodes.Unauthorized);
            }

            if (string.IsNullOrWhiteSpace(artifact.FileUrl) &&
                string.IsNullOrWhiteSpace(artifact.Content))
            {
                return Result.Failure<ArtifactDownloadDto>(
                    "Artifact content is not available yet.",
                    ErrorCodes.InvalidState);
            }

            // The transcript and the summary go out as plain text, whatever they are stored as.
            //
            // Their storage shapes are markdown and structured JSON, and both are right for the
            // things that READ them — the web client parses the summary JSON into prose and the
            // knowledge indexer reads the same field. Neither is right for a person who clicked
            // Download and got `**[Nam (VI)]**: xin chào` or a wall of `{"summary":…}`. Rendering
            // on the way out rather than changing the writer also fixes every artifact already in
            // the database; those rows are never rewritten.
            if (ArtifactPlainText.IsTextExport(artifact.ArtifactType))
            {
                return Result<ArtifactDownloadDto>.Success(new ArtifactDownloadDto(
                    // A text export never has a file behind it — the content IS the artifact — so
                    // there is no signed URL to produce here.
                    null,
                    ArtifactPlainText.Render(artifact.ArtifactType, artifact.Content),
                    $"warptalk-{artifact.ArtifactType.ToLowerInvariant()}-{artifact.Id:N}.txt",
                    "text/plain"));
            }

            // WT-432: LegacyMarkdownMime is here for the rows the finalizer wrote before it
            // learned to store a token instead of a MIME type. Without it those fall to the
            // default and download as .txt — which is what every artifact in production did.
            var extension = artifact.FileFormat?.ToLowerInvariant() switch
            {
                ArtifactFileFormats.Markdown or ArtifactFileFormats.LegacyMarkdownMime => "md",
                ArtifactFileFormats.Json => "json",
                ArtifactFileFormats.PlainText => "txt",
                "mp4" => "mp4",
                "webm" => "webm",
                "wav" => "wav",
                _ => artifact.ContainsRawAudio ? "bin" : "txt"
            };
            var contentType = artifact.FileFormat?.ToLowerInvariant() switch
            {
                ArtifactFileFormats.Markdown or ArtifactFileFormats.LegacyMarkdownMime => "text/markdown",
                ArtifactFileFormats.Json => "application/json",
                ArtifactFileFormats.PlainText => "text/plain",
                "mp4" => "video/mp4",
                "webm" => "video/webm",
                "wav" => "audio/wav",
                _ => artifact.ContainsRawAudio ? "application/octet-stream" : "text/plain"
            };
            var fileName = $"warptalk-{artifact.ArtifactType.ToLowerInvariant()}-{artifact.Id:N}.{extension}";
            var downloadUrl = string.IsNullOrWhiteSpace(artifact.FileUrl)
                ? null
                : await _urlSigner.CreateDownloadUrlAsync(
                    artifact.FileUrl,
                    TimeSpan.FromMinutes(15),
                    ct);
            return Result<ArtifactDownloadDto>.Success(new ArtifactDownloadDto(
                downloadUrl,
                artifact.Content,
                fileName,
                contentType));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting download URL for artifact {ArtifactId}", artifactId);
            return Result.Failure<ArtifactDownloadDto>("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// Releases the consent hold on an artifact — today, in practice, a recording
    /// (<c>RecordingCompletedEventProcessor</c> is the one writer that sets
    /// <c>ConsentRequired = true</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// HOST ONLY. This used to authorize with <see cref="ArtifactAccessHelper"/> — the very same
    /// predicate the download check at <see cref="GetArtifactDownloadAsync"/> uses — which made the
    /// consent gate self-serve: a participant refused a recording download could POST here, get a
    /// 204, and then download it. Consent granted by the person who benefits from it is not
    /// consent. The approver must be someone other than the requester, and the host is the only
    /// authority this row knows about.
    /// </para>
    /// <para>
    /// KNOWN AND DELIBERATELY UNCHANGED: consent is still recorded GLOBALLY. There is one boolean
    /// on the shared artifact row and no per-user grant table, so one host approval unlocks the
    /// recording for every participant at once — nobody can be granted or refused individually, and
    /// the release cannot be walked back per person. Making consent per-user needs a new grant
    /// table, and this release cycle is deliberately migration-free, so that is left for its own
    /// ticket. What changes here is only WHO may pull the lever, not how many people it opens the
    /// door for.
    /// </para>
    /// <para>
    /// Workspace Owners/Admins are not admitted, on purpose. The download path they would be
    /// approving does not admit them either (it is host, or participant-by-policy), so letting them
    /// approve a release they cannot themselves read would be a third spelling of "who runs this
    /// room" — the drift <c>RoomReadAccess</c> exists to stop.
    /// </para>
    /// </remarks>
    public async Task<Result> ApproveArtifactConsentAsync(Guid artifactId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var artifact = await _unitOfWork.TranslationRoomArtifactRepository.GetArtifactWithRoomAsync(artifactId, ct);

            if (artifact == null) return Result.Failure(TranslationRoomConstants.ErrorArtifactNotFound, ErrorCodes.NotFound);

            if (!artifact.TranslationRoom.IsHostedBy(userId))
                return Result.Failure(TranslationRoomConstants.ErrorUnauthorizedConsentArtifact, ErrorCodes.Unauthorized);

            artifact.ConsentRequired = false;
            _unitOfWork.TranslationRoomArtifactRepository.Update(artifact);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error approving consent for artifact {ArtifactId}", artifactId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpected, ErrorCodes.InternalServerError);
        }
    }
}

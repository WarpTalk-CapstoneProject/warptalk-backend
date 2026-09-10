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

    public async Task<Result<string>> RegenerateSummaryAsync(
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
                return Result.Failure<string>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            // Same two gates as reading the artifacts, for the same reasons: there is nothing
            // to summarise until the meeting is over, and re-summarising exposes the whole
            // transcript to whoever asks.
            if (!TranslationRoomConstants.TerminalStatuses.Contains(room.Status.ToString()))
                return Result.Failure<string>("A summary can only be rewritten for a finished meeting.", ErrorCodes.InvalidState);

            if (!ArtifactAccessHelper.HasAccessToRoomArtifacts(room, userId))
                return Result.Failure<string>("Unauthorized to summarise this room.", ErrorCodes.Unauthorized);

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
                    return Result.Failure<string>(
                        "This meeting has a transcript but no summary artifact to rewrite. It needs to be finalized again, not re-summarised.",
                        ErrorCodes.InvalidState);
                }

                // CARRYING WHAT THEY ASKED FOR, which is the whole difference between this
                // working and appearing not to.
                //
                // The redirect is right — the meeting needs finalizing, not re-summarising — but
                // it used to drop the shape and language on the way. The request returned
                // success, a General summary arrived, and the picker snapped back to General, so
                // from the host's side pressing "Standup" did nothing at all. And by this code's
                // own reasoning this is the meeting they are MOST likely to press it on: the one
                // showing no summary.
                _logger.LogInformation(
                    "Room {RoomId} has no artifacts at all; queueing finalization in {TemplateKey}/{Language} instead of a summary rewrite that would have nothing to land on.",
                    roomId,
                    NormalizeTemplateKey(templateKey),
                    LanguageHelper.NormalizeLanguageCode(summaryLanguage) is { Length: > 0 } asked ? asked : "as-spoken");
                _finalizationQueue.QueueFinalization(
                    roomId,
                    NormalizeTemplateKey(templateKey),
                    LanguageHelper.NormalizeLanguageCode(summaryLanguage));
                // No request id, because this did not go out as a summary request: finalization is
                // a different pipeline with a different answer. An empty id tells the caller there
                // is nothing to ask about rather than handing it one that will never resolve.
                return Result.Success(string.Empty);
            }

            var requestId = await QueueSummaryAsync(
                room,
                NormalizeTemplateKey(templateKey),
                LanguageHelper.NormalizeLanguageCode(summaryLanguage) ?? string.Empty,
                bearerToken,
                // REPLACES the room's summary. This is the host deciding what the meeting's
                // summary IS, which is a different act from a reader asking to see it in their
                // own language — see GetOrQueueSummaryVariantAsync.
                SummaryDelivery.Canonical);

            _logger.LogInformation(
                "Queued summary regeneration for room {RoomId} with template {TemplateKey} in language {SummaryLanguage}",
                roomId,
                templateKey,
                LanguageHelper.NormalizeLanguageCode(summaryLanguage) is { Length: > 0 } code ? code : "as-spoken");

            return Result.Success(requestId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue summary regeneration for room {RoomId}", roomId);
            return Result.Failure<string>("Could not queue the summary rewrite.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<SummaryRewriteStatusDto>> GetSummaryRewriteStatusAsync(
        Guid roomId,
        Guid userId,
        string requestId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return Result.Failure<SummaryRewriteStatusDto>("A request id is required.", ErrorCodes.InvalidState);

        var room = await _unitOfWork.TranslationRoomRepository.FirstOrDefaultAsync(
            r => r.Id == roomId,
            "TranslationRoomParticipants,TranslationRoomArtifacts",
            ct);

        if (room == null)
            return Result.Failure<SummaryRewriteStatusDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

        // The same gate as reading the artifacts, because the failure text quotes what the worker
        // found in this meeting's transcript — "This meeting has no saved transcript to summarise"
        // is itself a fact about the meeting.
        if (!ArtifactAccessHelper.HasAccessToRoomArtifacts(room, userId))
            return Result.Failure<SummaryRewriteStatusDto>("Unauthorized to read this room's artifacts.", ErrorCodes.Unauthorized);

        var stored = await _redisStateRepo.StringGetAsync(
            TranslationRoomConstants.SummaryRewriteStatusKeyPrefix + requestId);

        // NOTHING THERE IS "NOT YET", NEVER "DONE".
        //
        // The key is absent while the worker is still running, and absent again once it has
        // expired or been evicted — Redis runs allkeys-lru, so an outcome nobody collected can
        // simply be gone. Those are indistinguishable from here, and the honest answer to both is
        // that we do not know yet. Reading an absent key as success would turn every eviction
        // into a silent claim that the rewrite worked, which is the failure this whole endpoint
        // exists to remove.
        if (string.IsNullOrWhiteSpace(stored))
            return Result.Success(new SummaryRewriteStatusDto { Status = "pending" });

        try
        {
            var status = JsonSerializer.Deserialize<SummaryRewriteStatusDto>(stored);
            return Result.Success(status ?? new SummaryRewriteStatusDto { Status = "pending" });
        }
        catch (JsonException ex)
        {
            // Written by the consumer in this same service, so this is a bug rather than bad
            // input — but it must not take down the poll that the browser is depending on.
            _logger.LogError(ex, "Unreadable summary rewrite status for request {RequestId}", requestId);
            return Result.Success(new SummaryRewriteStatusDto { Status = "pending" });
        }
    }

    public async Task<Result<SummaryVariantDto>> GetOrQueueSummaryVariantAsync(
        Guid roomId,
        Guid userId,
        string templateKey,
        string? language,
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
                return Result.Failure<SummaryVariantDto>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            if (!TranslationRoomConstants.TerminalStatuses.Contains(room.Status.ToString()))
                return Result.Failure<SummaryVariantDto>("A summary is only available for a finished meeting.", ErrorCodes.InvalidState);

            // The same gate the canonical summary is read behind. A rendering is the same
            // meeting's content in another language — it must not be reachable by anyone the
            // original is not.
            if (!ArtifactAccessHelper.HasAccessToRoomArtifacts(room, userId))
                return Result.Failure<SummaryVariantDto>("Unauthorized to read this room's summary.", ErrorCodes.Unauthorized);

            var wantedTemplate = NormalizeTemplateKey(templateKey);
            var wantedLanguage = LanguageHelper.NormalizeLanguageCode(language) ?? string.Empty;

            var canonical = room.TranslationRoomArtifacts
                .Where(artifact => artifact.DeletedAt == null
                    && string.Equals(artifact.ArtifactType, ArtifactType.SUMMARY_EXPORT.ToString(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(artifact => artifact.CreatedAt)
                .FirstOrDefault();

            // NOTHING TO RENDER FROM, AND SAYING SO BEATS SPENDING A MODEL CALL.
            //
            // RegenerateSummaryAsync learned this the expensive way: on a meeting that was never
            // finalized the request succeeded, the worker read the transcript, spent an LLM call,
            // and the consumer dropped the result because there was no artifact to replace. A
            // rendering has the same precondition for the same reason — it is the published
            // summary in another language, and a meeting with no published summary has no
            // language to offer.
            if (canonical == null)
                return Result.Failure<SummaryVariantDto>(
                    "This meeting has no summary yet, so there is nothing to read in another language.",
                    ErrorCodes.InvalidState);

            // Is this pair the one the host published? Read off the canonical's OWN stamped
            // values rather than assuming general/as-spoken: a host who rewrote the meeting into
            // Standup made Standup the published shape, and serving them a cached copy of their
            // own summary would be a second row that drifts the moment they rewrite again.
            if (MatchesStoredSummary(canonical.Content, wantedTemplate, wantedLanguage))
            {
                return Result<SummaryVariantDto>.Success(new SummaryVariantDto(
                    wantedTemplate,
                    wantedLanguage,
                    canonical.Content,
                    IsCanonical: true,
                    SummaryVariantStatus.Ready,
                    canonical.UpdatedAt));
            }

            var cached = await _unitOfWork.TranslationRoomSummaryVariantRepository
                .GetAsync(roomId, wantedTemplate, wantedLanguage, ct);

            if (cached != null)
            {
                return Result<SummaryVariantDto>.Success(new SummaryVariantDto(
                    wantedTemplate,
                    wantedLanguage,
                    cached.Content,
                    IsCanonical: false,
                    SummaryVariantStatus.Ready,
                    cached.UpdatedAt));
            }

            await QueueSummaryAsync(room, wantedTemplate, wantedLanguage, bearerToken, SummaryDelivery.Variant);

            _logger.LogInformation(
                "Queued a {TemplateKey}/{Language} rendering of room {RoomId}'s summary for the first reader who asked",
                wantedTemplate,
                wantedLanguage is { Length: > 0 } ? wantedLanguage : "as-spoken",
                roomId);

            return Result<SummaryVariantDto>.Success(new SummaryVariantDto(
                wantedTemplate,
                wantedLanguage,
                Content: null,
                IsCanonical: false,
                SummaryVariantStatus.Generating,
                UpdatedAt: null));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read a summary rendering for room {RoomId}", roomId);
            return Result.Failure<SummaryVariantDto>("Could not read the summary.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<List<SummaryVariantSummaryDto>>> GetSummaryVariantsAsync(
        Guid roomId,
        Guid userId,
        CancellationToken ct = default)
    {
        try
        {
            var room = await _unitOfWork.TranslationRoomRepository.FirstOrDefaultAsync(
                r => r.Id == roomId,
                "TranslationRoomParticipants,TranslationRoomArtifacts",
                ct);

            if (room == null)
                return Result.Failure<List<SummaryVariantSummaryDto>>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            if (!ArtifactAccessHelper.HasAccessToRoomArtifacts(room, userId))
                return Result.Failure<List<SummaryVariantSummaryDto>>("Unauthorized to read this room's summary.", ErrorCodes.Unauthorized);

            var listed = new List<SummaryVariantSummaryDto>();

            var canonical = room.TranslationRoomArtifacts
                .Where(artifact => artifact.DeletedAt == null
                    && string.Equals(artifact.ArtifactType, ArtifactType.SUMMARY_EXPORT.ToString(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(artifact => artifact.CreatedAt)
                .FirstOrDefault();

            if (canonical != null)
            {
                var (template, language) = ReadStoredSummaryKey(canonical.Content);
                // UpdatedAt is nullable on an artifact and only set once something rewrites it,
                // so a summary written at finalization and never touched again has none. Its
                // creation IS when it was written, which is the answer the staleness check wants.
                listed.Add(new SummaryVariantSummaryDto(
                    template, language, IsCanonical: true, canonical.UpdatedAt ?? canonical.CreatedAt));
            }

            var variants = await _unitOfWork.TranslationRoomSummaryVariantRepository
                .GetByRoomIdAsync(roomId, ct);

            listed.AddRange(variants.Select(variant => new SummaryVariantSummaryDto(
                variant.TemplateKey,
                variant.Language,
                IsCanonical: false,
                variant.UpdatedAt)));

            return Result<List<SummaryVariantSummaryDto>>.Success(listed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list summary renderings for room {RoomId}", roomId);
            return Result.Failure<List<SummaryVariantSummaryDto>>("An unexpected error occurred.", ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// One place that publishes to `assistant:summary_requests`, so the two callers cannot drift
    /// on the fields the worker reads.
    ///
    /// <paramref name="delivery"/> is what SummaryResultConsumerWorker routes on when the answer
    /// comes back: `canonical` replaces the room's summary artifact, `variant` lands in the cache
    /// beside it. It travels on the request rather than being re-derived from the result, because
    /// only the caller knows which act this was — the content of a Standup-in-Japanese summary
    /// looks identical whether the host chose it for the room or one reader asked to see it.
    /// </summary>
    /// <summary>
    /// Queue one summary request and return the id it went out under.
    ///
    /// WT-669: the id used to be generated into the dictionary and forgotten on the same line.
    /// It is the only handle anybody has on a request once it is asynchronous — the worker
    /// echoes it back on the result, so it is what lets an outcome find its way to the person
    /// who asked rather than to a log.
    /// </summary>
    private async Task<string> QueueSummaryAsync(
        TranslationRoom room,
        string templateKey,
        string language,
        string? bearerToken,
        string delivery)
    {
        var targetLanguages = LanguageHelper.ParseTargetLanguages(room.TargetLanguages);
        var requestId = Guid.NewGuid().ToString();

        await _redisStateRepo.StreamAddAsync(SummaryRequestStream, new Dictionary<string, string>
        {
            ["request_id"] = requestId,
            ["room_id"] = room.Id.ToString(),
            ["workspace_id"] = room.WorkspaceId.ToString(),
            ["template_key"] = templateKey,
            // Forwarded so the worker reads the transcript AS THE CALLER, through the
            // same authenticated endpoint they could already use — never a privileged
            // bypass that would let a regeneration read more than its requester can.
            ["bearer_token"] = bearerToken ?? string.Empty,
            ["target_languages_json"] = JsonSerializer.Serialize(targetLanguages),
            // Normalised to a bare ISO 639-1 code, matching what the AI side keys its
            // language names by: a room stores `vi-VN` and a picker sends `vi`, and a
            // summary must not come out in a different language depending on which
            // spelling reached it. Empty means the caller expressed no preference.
            ["summary_language"] = language,
            ["delivery"] = delivery,
            ["timestamp_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
        });

        return requestId;
    }

    private static string NormalizeTemplateKey(string? templateKey) =>
        string.IsNullOrWhiteSpace(templateKey) ? "general" : templateKey.Trim().ToLowerInvariant();

    /// <summary>
    /// The (shape, language) a stored summary is actually in, read off the JSON the AI worker
    /// stamped rather than inferred. `templateKey` has been written since WT-530 and
    /// `summaryLanguage` since the language picker; a summary written before either reads as
    /// general/as-spoken, which is exactly what it was.
    /// </summary>
    private static (string TemplateKey, string Language) ReadStoredSummaryKey(string? contentJson)
    {
        if (string.IsNullOrWhiteSpace(contentJson)) return ("general", string.Empty);

        try
        {
            using var document = JsonDocument.Parse(contentJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return ("general", string.Empty);

            var template = document.RootElement.TryGetProperty("templateKey", out var templateNode)
                && templateNode.ValueKind == JsonValueKind.String
                    ? NormalizeTemplateKey(templateNode.GetString())
                    : "general";

            var language = document.RootElement.TryGetProperty("summaryLanguage", out var languageNode)
                && languageNode.ValueKind == JsonValueKind.String
                    ? LanguageHelper.NormalizeLanguageCode(languageNode.GetString()) ?? string.Empty
                    : string.Empty;

            return (template, language);
        }
        catch (JsonException)
        {
            // A summary whose content will not parse is still the room's summary. Treating it as
            // the default pair keeps it reachable rather than making every language request queue
            // a generation against an artifact nobody can read.
            return ("general", string.Empty);
        }
    }

    private static bool MatchesStoredSummary(string? contentJson, string templateKey, string language)
    {
        var stored = ReadStoredSummaryKey(contentJson);
        return stored.TemplateKey == templateKey && stored.Language == language;
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

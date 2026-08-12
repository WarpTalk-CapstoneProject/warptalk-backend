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

    // Consumed by warptalk-ai's SummaryTemplateWorker. Renaming either side silently is how
    // a request ends up with no consumer and no reply.
    private const string SummaryRequestStream = "assistant:summary_requests";

    public TranslationRoomArtifactService(
        IUnitOfWork unitOfWork,
        ILogger<TranslationRoomArtifactService> logger,
        IArtifactUrlSigner urlSigner,
        IRedisStateRepository redisStateRepo)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _urlSigner = urlSigner;
        _redisStateRepo = redisStateRepo;
    }

    public async Task<Result> RegenerateSummaryAsync(
        Guid roomId,
        Guid userId,
        string templateKey,
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
                ["timestamp_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
            });

            _logger.LogInformation(
                "Queued summary regeneration for room {RoomId} with template {TemplateKey}",
                roomId,
                templateKey);

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
                return Result.Failure<ArtifactDownloadDto>("Unauthorized to download this artifact.", ErrorCodes.Unauthorized);

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

            var extension = artifact.FileFormat?.ToLowerInvariant() switch
            {
                "markdown" => "md",
                "json" => "json",
                "text/plain" => "txt",
                "mp4" => "mp4",
                "webm" => "webm",
                "wav" => "wav",
                _ => artifact.ContainsRawAudio ? "bin" : "txt"
            };
            var contentType = artifact.FileFormat?.ToLowerInvariant() switch
            {
                "markdown" => "text/markdown",
                "json" => "application/json",
                "text/plain" => "text/plain",
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

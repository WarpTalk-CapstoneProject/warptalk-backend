using System;
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

    public TranslationRoomArtifactService(
        IUnitOfWork unitOfWork,
        ILogger<TranslationRoomArtifactService> logger,
        IArtifactUrlSigner urlSigner)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _urlSigner = urlSigner;
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

    public async Task<Result> ApproveArtifactConsentAsync(Guid artifactId, Guid userId, CancellationToken ct = default)
    {
        try
        {
            var artifact = await _unitOfWork.TranslationRoomArtifactRepository.GetArtifactWithRoomAsync(artifactId, ct);

            if (artifact == null) return Result.Failure(TranslationRoomConstants.ErrorArtifactNotFound, ErrorCodes.NotFound);

            if (!ArtifactAccessHelper.HasAccessToRoomArtifacts(artifact.TranslationRoom, userId))
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

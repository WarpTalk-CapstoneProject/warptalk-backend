using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.TranscriptService.Application.Mappers;
using WarpTalk.TranscriptService.Domain.Interfaces;
using GetParticipantsByRoomIdRequest = WarpTalk.Shared.Protos.GetParticipantsByRoomIdRequest;
using GetTranslationRoomRequest = WarpTalk.Shared.Protos.GetTranslationRoomRequest;
using TranslationRoomServiceClient = WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient;

namespace WarpTalk.TranscriptService.Application.Services;

public class TranscriptQueryService : ITranscriptQueryService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly TranslationRoomServiceClient _roomClient;
    private readonly ILogger<TranscriptQueryService> _logger;

    public TranscriptQueryService(
        IUnitOfWork unitOfWork,
        TranslationRoomServiceClient roomClient,
        ILogger<TranscriptQueryService> logger)
    {
        _unitOfWork = unitOfWork;
        _roomClient = roomClient;
        _logger = logger;
    }

    public async Task<Result<TranscriptDto>> GetTranscriptAsync(Guid transcriptId, Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var transcript = await _unitOfWork.Transcripts.GetByIdAsync(transcriptId, cancellationToken);
            if (transcript == null || transcript.DeletedAt != null)
                return Result.Failure<TranscriptDto>($"Transcript with ID {transcriptId} not found.", "NOT_FOUND");

            if (!await CanAccessTranscriptAsync(transcript, userId, cancellationToken))
                return Result.Failure<TranscriptDto>("You do not have access to this transcript.", "FORBIDDEN");

            return Result.Success(transcript.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting transcript {TranscriptId}", transcriptId);
            return Result.Failure<TranscriptDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<TranscriptDto>> GetTranscriptByTranslationRoomAsync(Guid translationRoomId, Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var transcript = await _unitOfWork.Transcripts.FirstOrDefaultAsync(
                t => t.TranslationRoomId == translationRoomId && t.DeletedAt == null,
                cancellationToken);

            if (transcript == null)
                return Result.Failure<TranscriptDto>($"Transcript for room {translationRoomId} not found.", "NOT_FOUND");

            if (!await CanAccessTranscriptAsync(transcript, userId, cancellationToken))
                return Result.Failure<TranscriptDto>("You do not have access to this transcript.", "FORBIDDEN");

            return Result.Success(transcript.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting transcript for room {TranslationRoomId}", translationRoomId);
            return Result.Failure<TranscriptDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<PagedResult<TranscriptSegmentDto>>> GetSegmentsAsync(Guid transcriptId, Guid userId, int skip = 0, int take = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            var transcript = await _unitOfWork.Transcripts.GetByIdAsync(transcriptId, cancellationToken);
            if (transcript == null || transcript.DeletedAt != null)
            {
                return Result.Failure<PagedResult<TranscriptSegmentDto>>($"Transcript with ID {transcriptId} not found.", "NOT_FOUND");
            }

            if (!await CanAccessTranscriptAsync(transcript, userId, cancellationToken))
                return Result.Failure<PagedResult<TranscriptSegmentDto>>("You do not have access to this transcript.", "FORBIDDEN");

            var totalCount = await _unitOfWork.TranscriptSegments.CountAsync(s => s.TranscriptId == transcriptId, cancellationToken);
            var segments = await _unitOfWork.TranscriptSegments.GetPagedAsync(
                s => s.TranscriptId == transcriptId,
                skip,
                take,
                q => q.OrderBy(s => s.SequenceOrder),
                cancellationToken);

            var result = new PagedResult<TranscriptSegmentDto>(
                totalCount,
                segments.Select(s => s.ToDto())
            );

            return Result.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting segments for transcript {TranscriptId}", transcriptId);
            return Result.Failure<PagedResult<TranscriptSegmentDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<PagedResult<TranscriptTranslationDto>>> GetTranslationsAsync(Guid transcriptId, Guid userId, int skip = 0, int take = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            var transcript = await _unitOfWork.Transcripts.GetByIdAsync(transcriptId, cancellationToken);
            if (transcript == null || transcript.DeletedAt != null)
            {
                return Result.Failure<PagedResult<TranscriptTranslationDto>>($"Transcript with ID {transcriptId} not found.", "NOT_FOUND");
            }

            if (!await CanAccessTranscriptAsync(transcript, userId, cancellationToken))
                return Result.Failure<PagedResult<TranscriptTranslationDto>>("You do not have access to this transcript.", "FORBIDDEN");

            var totalCount = await _unitOfWork.TranscriptTranslations.CountAsync(t => t.Segment.TranscriptId == transcriptId, cancellationToken);
            var translations = await _unitOfWork.TranscriptTranslations.GetPagedAsync(
                t => t.Segment.TranscriptId == transcriptId,
                skip,
                take,
                q => q.OrderBy(t => t.Segment.SequenceOrder),
                cancellationToken);

            var result = new PagedResult<TranscriptTranslationDto>(
                totalCount,
                translations.Select(t => t.ToDto())
            );

            return Result.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting translations for transcript {TranscriptId}", transcriptId);
            return Result.Failure<PagedResult<TranscriptTranslationDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    private async Task<bool> CanAccessTranscriptAsync(WarpTalk.TranscriptService.Domain.Entities.Transcript transcript, Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var room = await _roomClient.GetTranslationRoomByIdAsync(
                new GetTranslationRoomRequest { Id = transcript.TranslationRoomId.ToString() },
                cancellationToken: cancellationToken);

            if (Guid.TryParse(room.HostId, out var hostId) && hostId == userId)
                return true;

            var participants = await _roomClient.GetParticipantsByRoomIdAsync(
                new GetParticipantsByRoomIdRequest { RoomId = transcript.TranslationRoomId.ToString() },
                cancellationToken: cancellationToken);

            return participants.Participants.Any(p =>
                Guid.TryParse(p.Id, out var participantUserId) &&
                participantUserId == userId);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return false;
        }
    }
}

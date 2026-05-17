using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.TranscriptService.Application.Mappers;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Enums;
using WarpTalk.TranscriptService.Domain.Interfaces;
using TranslationRoomServiceClient = WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient;
using GetTranslationRoomRequest = WarpTalk.Shared.Protos.GetTranslationRoomRequest;

namespace WarpTalk.TranscriptService.Application.Services;

public class TranscriptCorrectionService : ITranscriptCorrectionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly TranslationRoomServiceClient _roomClient;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<TranscriptCorrectionService> _logger;

    public TranscriptCorrectionService(
        IUnitOfWork unitOfWork,
        TranslationRoomServiceClient roomClient,
        IConnectionMultiplexer redis,
        ILogger<TranscriptCorrectionService> logger)
    {
        _unitOfWork = unitOfWork;
        _roomClient = roomClient;
        _redis = redis;
        _logger = logger;
    }

    public async Task<Result> SubmitCorrectionAsync(Guid segmentId, CreateCorrectionDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            var segment = await _unitOfWork.TranscriptSegments.GetByIdAsync(segmentId, cancellationToken);
            if (segment == null)
                return Result.Failure($"Segment with ID {segmentId} not found.", "NOT_FOUND");

            var transcript = await _unitOfWork.Transcripts.GetByIdAsync(segment.TranscriptId, cancellationToken);
            if (transcript == null)
                return Result.Failure($"Transcript with ID {segment.TranscriptId} not found.", "NOT_FOUND");

            if (transcript.Status != TranscriptStatus.Finalized)
                return Result.Failure("Corrections can only be submitted for finalized transcripts.", "BAD_REQUEST");

            var roomResponse = await _roomClient.GetTranslationRoomByIdAsync(
                new GetTranslationRoomRequest { Id = transcript.TranslationRoomId.ToString() },
                cancellationToken: cancellationToken);

            if (roomResponse == null)
                return Result.Failure("Unable to verify room permissions.", "UNAUTHORIZED");

            var correction = dto.ToEntity(segmentId);

            segment.IsCorrected = true;
            if (dto.CorrectionType == CorrectionType.Stt)
            {
                segment.OriginalText = dto.CorrectedText;
            }

            _unitOfWork.TranscriptSegments.Update(segment);
            await _unitOfWork.TranscriptCorrections.AddAsync(correction, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var db = _redis.GetDatabase();
            var streamKey = $"translate:requests:{transcript.TranslationRoomId}";
            
            await db.StreamAddAsync(streamKey, new NameValueEntry[]
            {
                new("segment_id", segmentId.ToString()),
                new("transcript_id", transcript.Id.ToString()),
                new("room_id", transcript.TranslationRoomId.ToString()),
                new("source_language", transcript.SourceLanguage),
                new("text", dto.CorrectedText),
                new("is_correction", "true")
            });

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error submitting correction for segment {SegmentId}", segmentId);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<IEnumerable<TranscriptCorrectionDto>>> GetCorrectionsBySegmentIdAsync(Guid segmentId, CancellationToken cancellationToken = default)
    {
        try
        {
            var corrections = await _unitOfWork.TranscriptCorrections.FindAsync(c => c.SegmentId == segmentId, cancellationToken);
            return Result.Success(corrections.Select(c => c.ToDto()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting corrections for segment {SegmentId}", segmentId);
            return Result.Failure<IEnumerable<TranscriptCorrectionDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

}

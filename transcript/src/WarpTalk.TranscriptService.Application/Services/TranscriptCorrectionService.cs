using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Grpc.Core;
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.TranscriptService.Application.Mappers;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Enums;
using WarpTalk.TranscriptService.Domain.Interfaces;
using GetParticipantsByRoomIdRequest = WarpTalk.Shared.Protos.GetParticipantsByRoomIdRequest;
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

    public async Task<Result> SubmitCorrectionAsync(Guid transcriptId, Guid segmentId, Guid userId, CreateCorrectionDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            var segment = await _unitOfWork.TranscriptSegments.GetByIdAsync(segmentId, cancellationToken);
            if (segment == null)
                return Result.Failure($"Segment with ID {segmentId} not found.", "NOT_FOUND");

            if (segment.TranscriptId != transcriptId)
                return Result.Failure($"Segment {segmentId} does not belong to transcript {transcriptId}.", "NOT_FOUND");

            var transcript = await _unitOfWork.Transcripts.GetByIdAsync(transcriptId, cancellationToken);
            if (transcript == null)
                return Result.Failure($"Transcript with ID {segment.TranscriptId} not found.", "NOT_FOUND");

            if (string.Equals(transcript.Status, "ARCHIVED", StringComparison.OrdinalIgnoreCase))
                return Result.Failure("Archived transcripts cannot be corrected.", "BAD_REQUEST");

            if (!await CanAccessTranscriptAsync(transcript, userId, cancellationToken))
                return Result.Failure("You do not have access to this transcript.", "UNAUTHORIZED");

            var correction = dto.ToEntity(segmentId, userId);
            var isMtCorrection = dto.CorrectionType?.Equals("MT", StringComparison.OrdinalIgnoreCase) == true;

            if (isMtCorrection)
            {
                if (string.IsNullOrWhiteSpace(dto.TargetLanguage))
                    return Result.Failure("TargetLanguage is required for MT corrections.", "BAD_REQUEST");

                // Link the correction to the translation_contents row it's actually correcting —
                // the current SegmentTranslationLink for this segment/language. Re-translation
                // itself happens asynchronously: the translate:requests message pushed below is
                // picked up by translate_worker, and the resulting translate:results message is
                // what actually supersedes this link (TranscriptRedisConsumerService.
                // ProcessTranslateMessageAsync already flips IsCurrent on re-translation).
                var currentLink = (await _unitOfWork.SegmentTranslationLinks.FindAsync(
                        l => l.SegmentId == segmentId && l.TargetLanguage == dto.TargetLanguage && l.IsCurrent,
                        cancellationToken))
                    .FirstOrDefault();
                correction.TranslationContentId = currentLink?.TranslationContentId;

                // ReversalCreditTransactionId is intentionally left null here: reversing the prior
                // TRANSLATION charge requires a Billing gRPC endpoint that can look up and reverse
                // subscription.credit_transactions by (segment_id, target_lang) — that endpoint
                // doesn't exist yet (billing_worker only charges forward, see
                // warptalk-ai/billing_worker/worker.py). Tracked as a follow-up, not fabricated here.
            }
            else if (dto.CorrectionType?.Equals("STT", StringComparison.OrdinalIgnoreCase) == true)
            {
                segment.OriginalText = dto.CorrectedText;
            }

            segment.IsCorrected = true;

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

    public async Task<Result<IEnumerable<TranscriptCorrectionDto>>> GetCorrectionsBySegmentIdAsync(Guid transcriptId, Guid segmentId, Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var segment = await _unitOfWork.TranscriptSegments.GetByIdAsync(segmentId, cancellationToken);
            if (segment == null || segment.TranscriptId != transcriptId)
                return Result.Failure<IEnumerable<TranscriptCorrectionDto>>($"Segment with ID {segmentId} not found.", "NOT_FOUND");

            var transcript = await _unitOfWork.Transcripts.GetByIdAsync(transcriptId, cancellationToken);
            if (transcript == null)
                return Result.Failure<IEnumerable<TranscriptCorrectionDto>>($"Transcript with ID {transcriptId} not found.", "NOT_FOUND");

            if (!await CanAccessTranscriptAsync(transcript, userId, cancellationToken))
                return Result.Failure<IEnumerable<TranscriptCorrectionDto>>("You do not have access to this transcript.", "UNAUTHORIZED");

            var corrections = await _unitOfWork.TranscriptCorrections.FindAsync(c => c.SegmentId == segmentId, cancellationToken);
            return Result.Success(corrections.Select(c => c.ToDto()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting corrections for segment {SegmentId}", segmentId);
            return Result.Failure<IEnumerable<TranscriptCorrectionDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result> FinalizeTranscriptAsync(
        Guid transcriptId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var transcript = await _unitOfWork.Transcripts.GetByIdAsync(transcriptId, cancellationToken);
            if (transcript == null || transcript.DeletedAt != null)
                return Result.Failure($"Transcript with ID {transcriptId} not found.", "NOT_FOUND");

            var room = await _roomClient.GetTranslationRoomByIdAsync(
                new GetTranslationRoomRequest { Id = transcript.TranslationRoomId.ToString() },
                cancellationToken: cancellationToken);
            if (!Guid.TryParse(room.HostId, out var hostId) || hostId != userId)
                return Result.Failure("Only the meeting host can finalize the transcript.", "UNAUTHORIZED");

            if (string.Equals(transcript.Status, "ARCHIVED", StringComparison.OrdinalIgnoreCase))
                return Result.Failure("Archived transcripts cannot be finalized.", "BAD_REQUEST");

            if (string.Equals(transcript.Status, "FINALIZED", StringComparison.OrdinalIgnoreCase))
                return Result.Success();

            var now = DateTime.UtcNow;
            transcript.Status = "FINALIZED";
            transcript.IsActive = false;
            transcript.FinalizedAt = now;
            transcript.UpdatedAt = now;
            transcript.UpdatedBy = userId;
            _unitOfWork.Transcripts.Update(transcript);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return Result.Failure("Translation room not found.", "NOT_FOUND");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finalizing transcript {TranscriptId}", transcriptId);
            return Result.Failure("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    private async Task<bool> CanAccessTranscriptAsync(Transcript transcript, Guid userId, CancellationToken cancellationToken)
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

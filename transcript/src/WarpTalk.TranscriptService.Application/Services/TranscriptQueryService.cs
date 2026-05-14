using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.TranscriptService.Application.Mappers;
using WarpTalk.TranscriptService.Domain.Interfaces;

namespace WarpTalk.TranscriptService.Application.Services;

public class TranscriptQueryService : ITranscriptQueryService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<TranscriptQueryService> _logger;

    public TranscriptQueryService(IUnitOfWork unitOfWork, ILogger<TranscriptQueryService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<PagedResult<TranscriptSegmentDto>>> GetSegmentsAsync(Guid transcriptId, int skip = 0, int take = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            var transcriptExists = await _unitOfWork.Transcripts.ExistsAsync(t => t.Id == transcriptId, cancellationToken);
            if (!transcriptExists)
            {
                return Result.Failure<PagedResult<TranscriptSegmentDto>>($"Transcript with ID {transcriptId} not found.", "NOT_FOUND");
            }

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

    public async Task<Result<PagedResult<TranscriptTranslationDto>>> GetTranslationsAsync(Guid transcriptId, int skip = 0, int take = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            var transcriptExists = await _unitOfWork.Transcripts.ExistsAsync(t => t.Id == transcriptId, cancellationToken);
            if (!transcriptExists)
            {
                return Result.Failure<PagedResult<TranscriptTranslationDto>>($"Transcript with ID {transcriptId} not found.", "NOT_FOUND");
            }

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
}

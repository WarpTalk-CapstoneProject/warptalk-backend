using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.DTOs;

namespace WarpTalk.TranscriptService.Application.Interfaces;

public interface ITranscriptQueryService
{
    Task<Result<TranscriptDto>> GetTranscriptAsync(Guid transcriptId, Guid userId, CancellationToken cancellationToken = default);
    Task<Result<TranscriptDto>> GetTranscriptByTranslationRoomAsync(Guid translationRoomId, Guid userId, CancellationToken cancellationToken = default);
    Task<Result<PagedResult<TranscriptSegmentDto>>> GetSegmentsAsync(Guid transcriptId, Guid userId, int skip = 0, int take = 50, CancellationToken cancellationToken = default);
    Task<Result<PagedResult<TranscriptTranslationDto>>> GetTranslationsAsync(Guid transcriptId, Guid userId, int skip = 0, int take = 50, CancellationToken cancellationToken = default);
}

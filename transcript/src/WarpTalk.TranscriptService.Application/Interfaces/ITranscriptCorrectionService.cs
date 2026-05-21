using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.DTOs;

namespace WarpTalk.TranscriptService.Application.Interfaces;

public interface ITranscriptCorrectionService
{
    Task<Result> SubmitCorrectionAsync(Guid transcriptId, Guid segmentId, Guid userId, CreateCorrectionDto dto, CancellationToken cancellationToken = default);
    Task<Result<IEnumerable<TranscriptCorrectionDto>>> GetCorrectionsBySegmentIdAsync(Guid transcriptId, Guid segmentId, Guid userId, CancellationToken cancellationToken = default);
}

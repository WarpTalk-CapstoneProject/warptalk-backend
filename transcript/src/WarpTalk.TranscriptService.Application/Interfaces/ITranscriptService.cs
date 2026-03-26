using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.DTOs;

namespace WarpTalk.TranscriptService.Application.Interfaces;

public interface ITranscriptService
{
    Task<Result<TranscriptDto>> StartTranscriptAsync(CreateTranscriptRequest request, CancellationToken ct = default);
    Task<Result<TranscriptDto>> GetTranscriptAsync(Guid transcriptId, CancellationToken ct = default);
    Task<Result> ProcessAudioChunkAsync(Guid transcriptId, byte[] audioData, CancellationToken ct = default);
    Task<Result> FinalizeTranscriptAsync(Guid transcriptId, CancellationToken ct = default);
}

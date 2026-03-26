using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;

namespace WarpTalk.TranscriptService.Application.Services;

public class TranscriptService : ITranscriptService
{
    private readonly IUnitOfWork _unitOfWork;

    public TranscriptService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<TranscriptDto>> StartTranscriptAsync(CreateTranscriptRequest request, CancellationToken ct = default)
    {
        var transcript = new Transcript
        {
            Id = Guid.NewGuid(),
            MeetingId = request.MeetingId,
            Version = 1,
            Status = "recording",
            SourceLanguage = request.SourceLanguage,
            TotalSegments = 0,
            TotalDurationMs = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var repo = _unitOfWork.Transcripts;
        await repo.AddAsync(transcript);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(MapToDto(transcript));
    }

    public async Task<Result<TranscriptDto>> GetTranscriptAsync(Guid transcriptId, CancellationToken ct = default)
    {
        var repo = _unitOfWork.Transcripts;
        var transcript = await repo.GetByIdAsync(transcriptId);
        
        if (transcript == null)
            return Result.Failure<TranscriptDto>("Transcript not found", ErrorCodes.NotFound);

        return Result.Success(MapToDto(transcript));
    }

    public async Task<Result> ProcessAudioChunkAsync(Guid transcriptId, byte[] audioData, CancellationToken ct = default)
    {
        var repo = _unitOfWork.Transcripts;
        var transcript = await repo.GetByIdAsync(transcriptId);
        
        if (transcript == null || transcript.Status != "recording")
            return Result.Failure("Transcript not found or not recording", ErrorCodes.InvalidState);

        // Mocking the transcription process
        transcript.TotalSegments += 1;
        transcript.TotalDurationMs += audioData.Length; // mock logic
        transcript.UpdatedAt = DateTime.UtcNow;

        repo.Update(transcript);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success();
    }

    public async Task<Result> FinalizeTranscriptAsync(Guid transcriptId, CancellationToken ct = default)
    {
        var repo = _unitOfWork.Transcripts;
        var transcript = await repo.GetByIdAsync(transcriptId);
        
        if (transcript == null)
            return Result.Failure("Transcript not found", ErrorCodes.NotFound);

        transcript.Status = "finalized";
        transcript.FinalizedAt = DateTime.UtcNow;
        transcript.UpdatedAt = DateTime.UtcNow;

        repo.Update(transcript);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success();
    }

    private TranscriptDto MapToDto(Transcript t) =>
        new TranscriptDto(t.Id, t.MeetingId, t.Version, t.Status, t.SourceLanguage, t.TotalSegments, t.TotalDurationMs, t.CreatedAt, t.UpdatedAt, t.FinalizedAt);
}

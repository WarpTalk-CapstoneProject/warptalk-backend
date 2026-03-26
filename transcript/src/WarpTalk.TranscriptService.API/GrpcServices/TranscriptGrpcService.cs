using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.TranscriptService.Domain.Interfaces;

namespace WarpTalk.TranscriptService.API.GrpcServices;

public class TranscriptGrpcService : WarpTalk.Shared.Protos.TranscriptService.TranscriptServiceBase
{
    private readonly IUnitOfWork _unitOfWork;

    public TranscriptGrpcService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public override async Task<GetTranscriptResponse> GetTranscriptById(GetTranscriptRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var parsedId))
            throw GrpcErrors.InvalidId("Transcript");

        var transcript = await _unitOfWork.Transcripts.GetByIdAsync(parsedId);

        if (transcript == null || transcript.DeletedAt != null)
            throw GrpcErrors.NotFound("Transcript", request.Id);

        return MapToResponse(transcript);
    }

    public override async Task<GetTranscriptsByMeetingResponse> GetTranscriptsByMeetingId(GetTranscriptsByMeetingRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.MeetingId, out var parsedMeetingId))
            throw GrpcErrors.InvalidId("Meeting");

        var allTranscripts = await _unitOfWork.Transcripts.GetAllAsync();
        var meetingTranscripts = allTranscripts
            .Where(t => t.MeetingId == parsedMeetingId && t.DeletedAt == null)
            .OrderByDescending(t => t.Version)
            .ToList();

        var response = new GetTranscriptsByMeetingResponse();
        response.Transcripts.AddRange(meetingTranscripts.Select(MapToResponse));
        return response;
    }

    private static GetTranscriptResponse MapToResponse(Domain.Entities.Transcript t) => new()
    {
        Id = t.Id.ToString(),
        MeetingId = t.MeetingId.ToString(),
        Version = t.Version,
        Status = t.Status,
        SourceLanguage = t.SourceLanguage,
        TotalSegments = t.TotalSegments,
        TotalDurationMs = t.TotalDurationMs,
        CreatedAt = t.CreatedAt.ToString("O"),
        FinalizedAt = t.FinalizedAt?.ToString("O") ?? string.Empty
    };
}

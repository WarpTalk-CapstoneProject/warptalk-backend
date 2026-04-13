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

    public override async Task<GetTranscriptsByTranslationRoomResponse> GetTranscriptsByTranslationRoomId(GetTranscriptsByTranslationRoomRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.TranslationRoomId, out var parsedTranslationRoomId))
            throw GrpcErrors.InvalidId("TranslationRoom");

        var allTranscripts = await _unitOfWork.Transcripts.GetAllAsync();
        var translationRoomTranscripts = allTranscripts
            .Where(t => t.TranslationRoomId == parsedTranslationRoomId && t.DeletedAt == null)
            .OrderByDescending(t => t.Version)
            .ToList();

        var response = new GetTranscriptsByTranslationRoomResponse();
        response.Transcripts.AddRange(translationRoomTranscripts.Select(MapToResponse));
        return response;
    }

    private static GetTranscriptResponse MapToResponse(Domain.Entities.Transcript t) => new()
    {
        Id = t.Id.ToString(),
        TranslationRoomId = t.TranslationRoomId.ToString(),
        Version = t.Version,
        Status = t.Status,
        SourceLanguage = t.SourceLanguage,
        TotalSegments = t.TotalSegments,
        TotalDurationMs = t.TotalDurationMs,
        CreatedAt = t.CreatedAt.ToString("O"),
        FinalizedAt = t.FinalizedAt?.ToString("O") ?? string.Empty
    };
}

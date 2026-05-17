using System;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Protos;
using WarpTalk.TranscriptService.Domain.Interfaces;

namespace WarpTalk.TranscriptService.API.GrpcServices;

public class TranscriptGrpcService : WarpTalk.Shared.Protos.TranscriptService.TranscriptServiceBase
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<TranscriptGrpcService> _logger;

    public TranscriptGrpcService(IUnitOfWork unitOfWork, ILogger<TranscriptGrpcService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public override async Task<GetTranscriptResponse> GetTranscriptById(GetTranscriptRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var id))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Transcript ID"));
        }

        var transcript = await _unitOfWork.Transcripts.GetByIdAsync(id, context.CancellationToken);
        if (transcript == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Transcript not found"));
        }

        return new GetTranscriptResponse
        {
            Id = transcript.Id.ToString(),
            TranslationRoomId = transcript.TranslationRoomId.ToString(),
            Version = transcript.Version,
            Status = transcript.Status.ToString(),
            SourceLanguage = transcript.SourceLanguage ?? "unknown",
            TotalSegments = transcript.TotalSegments,
            TotalDurationMs = transcript.TotalDurationMs,
            CreatedAt = transcript.CreatedAt.ToString("O"),
            FinalizedAt = transcript.FinalizedAt?.ToString("O") ?? ""
        };
    }

    public override async Task<GetTranscriptsByTranslationRoomResponse> GetTranscriptsByTranslationRoomId(GetTranscriptsByTranslationRoomRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.TranslationRoomId, out var roomId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Room ID"));
        }

        var transcripts = await _unitOfWork.Transcripts
            .FindAsync(t => t.TranslationRoomId == roomId, context.CancellationToken);

        var response = new GetTranscriptsByTranslationRoomResponse();
        response.Transcripts.AddRange(transcripts.Select(t => new GetTranscriptResponse
        {
            Id = t.Id.ToString(),
            TranslationRoomId = t.TranslationRoomId.ToString(),
            Version = t.Version,
            Status = t.Status.ToString(),
            SourceLanguage = t.SourceLanguage ?? "unknown",
            TotalSegments = t.TotalSegments,
            TotalDurationMs = t.TotalDurationMs,
            CreatedAt = t.CreatedAt.ToString("O"),
            FinalizedAt = t.FinalizedAt?.ToString("O") ?? ""
        }));

        return response;
    }

    public override async Task<GetTranscriptSegmentsResponse> GetTranscriptSegments(GetTranscriptSegmentsRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.TranscriptId, out var transcriptId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Transcript ID"));
        }

        var skip = request.Skip > 0 ? request.Skip : 0;
        var take = request.Take > 0 ? request.Take : 50;

        var totalCount = await _unitOfWork.TranscriptSegments.CountAsync(s => s.TranscriptId == transcriptId, context.CancellationToken);
        var segments = await _unitOfWork.TranscriptSegments.GetPagedAsync(
            s => s.TranscriptId == transcriptId,
            skip,
            take,
            q => q.OrderBy(s => s.SequenceOrder),
            context.CancellationToken);

        var response = new GetTranscriptSegmentsResponse
        {
            TotalCount = totalCount
        };

        response.Segments.AddRange(segments.Select(s => new TranscriptSegmentDto
        {
            Id = s.Id.ToString(),
            SpeakerParticipantId = s.SpeakerParticipantId.ToString(),
            SpeakerName = s.SpeakerName ?? "Unknown",
            OriginalText = s.OriginalText ?? "",
            OriginalLanguage = s.OriginalLanguage ?? "unknown",
            Confidence = (double)s.Confidence,
            StartTimeMs = s.StartTimeMs,
            EndTimeMs = s.EndTimeMs,
            SequenceOrder = s.SequenceOrder
        }));

        return response;
    }

    public override async Task<GetTranscriptTranslationsResponse> GetTranscriptTranslations(GetTranscriptTranslationsRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.TranscriptId, out var transcriptId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid Transcript ID"));
        }

        var skip = request.Skip > 0 ? request.Skip : 0;
        var take = request.Take > 0 ? request.Take : 50;

        // Since Translations are children of Segments, we query translations that belong to segments of the transcript
        var totalCount = await _unitOfWork.TranscriptTranslations.CountAsync(
            t => t.Segment.TranscriptId == transcriptId, context.CancellationToken);

        var translations = await _unitOfWork.TranscriptTranslations.GetPagedAsync(
            t => t.Segment.TranscriptId == transcriptId,
            skip,
            take,
            q => q.OrderBy(t => t.Segment.SequenceOrder),
            context.CancellationToken);

        var response = new GetTranscriptTranslationsResponse
        {
            TotalCount = totalCount
        };

        response.Translations.AddRange(translations.Select(t => new TranscriptTranslationDto
        {
            Id = t.Id.ToString(),
            SegmentId = t.SegmentId.ToString(),
            TargetLanguage = t.TargetLanguage ?? "unknown",
            TranslatedText = t.TranslatedText ?? "",
            TranslatorModel = t.TranslatorModel ?? "",
            Confidence = (double)(t.Confidence ?? 1.0m),
            IsRetranslated = t.IsRetranslated,
            LatencyMs = t.LatencyMs ?? 0
        }));

        return response;
    }
}

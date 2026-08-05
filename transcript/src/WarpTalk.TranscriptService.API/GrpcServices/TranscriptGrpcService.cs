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

        response.Segments.AddRange(segments.Select(s =>
        {
            var dto = new TranscriptSegmentDto
            {
                Id = s.Id.ToString(),
                SpeakerParticipantId = s.SpeakerParticipantId.ToString(),
                SpeakerName = s.SpeakerName ?? "Unknown",
                OriginalText = s.OriginalText ?? "",
                OriginalLanguage = s.OriginalLanguage ?? "unknown",
                StartTimeMs = s.StartTimeMs,
                EndTimeMs = s.EndTimeMs,
                SequenceOrder = s.SequenceOrder
            };

            // WT-277: leave the optional field unset when the column is NULL. It used to coalesce
            // to 0, which for an avg_logprob is a *perfect* score — the opposite of "unknown".
            if (s.Confidence.HasValue)
            {
                dto.Confidence = (double)s.Confidence.Value;
            }

            return dto;
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

        // TranscriptTranslation (the old 1:1 table) was dropped — translations now live in
        // TranslationContent (deduplicated by workspace/text-hash/target-language), joined to
        // a segment via the current SegmentTranslationLink. Same join TranscriptExportService.
        // DownloadExportAsync and TranscriptQueryService.GetTranslationsAsync already use.
        var segments = (await _unitOfWork.TranscriptSegments.FindAsync(s => s.TranscriptId == transcriptId, context.CancellationToken)).ToList();
        var sequenceBySegmentId = segments.ToDictionary(s => s.Id, s => s.SequenceOrder);
        var segmentIds = segments.Select(s => s.Id).ToList();

        var currentLinks = (await _unitOfWork.SegmentTranslationLinks.FindAsync(
                l => segmentIds.Contains(l.SegmentId) && l.IsCurrent, context.CancellationToken))
            .OrderBy(l => sequenceBySegmentId.GetValueOrDefault(l.SegmentId))
            .ToList();

        var contentIds = currentLinks.Select(l => l.TranslationContentId).Distinct().ToList();
        var contentById = (await _unitOfWork.TranslationContents.FindAsync(c => contentIds.Contains(c.Id), context.CancellationToken))
            .ToDictionary(c => c.Id);

        var totalCount = currentLinks.Count;

        var response = new GetTranscriptTranslationsResponse
        {
            TotalCount = totalCount
        };

        response.Translations.AddRange(currentLinks
            .Skip(skip)
            .Take(take)
            .Where(l => contentById.ContainsKey(l.TranslationContentId))
            .Select(l =>
            {
                var content = contentById[l.TranslationContentId];
                var dto = new TranscriptTranslationDto
                {
                    Id = content.Id.ToString(),
                    SegmentId = l.SegmentId.ToString(),
                    TargetLanguage = l.TargetLanguage ?? "unknown",
                    TranslatedText = content.TranslatedText ?? "",
                    TranslatorModel = content.TranslatorModel ?? "",
                    IsRetranslated = content.IsRetranslated,
                    LatencyMs = content.LatencyMs ?? 0
                };

                // WT-277/WT-278: leave unset when unknown. This used to default to 1.0 — a
                // maximum-confidence score, invented here, for a translation that was never scored.
                if (content.SourceSttConfidence.HasValue)
                {
                    dto.SourceSttConfidence = (double)content.SourceSttConfidence.Value;
                }

                return dto;
            }));

        return response;
    }
}

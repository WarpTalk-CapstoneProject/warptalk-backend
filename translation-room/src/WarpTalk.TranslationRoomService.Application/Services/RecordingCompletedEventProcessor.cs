using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

public sealed class RecordingCompletedEventProcessor : IRecordingCompletedEventProcessor
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<RecordingCompletedEventProcessor> _logger;

    public RecordingCompletedEventProcessor(
        IUnitOfWork unitOfWork,
        ILogger<RecordingCompletedEventProcessor> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<bool>> ProcessAsync(
        EventEnvelope<MeetingRecordingCompletedEventPayload> envelope,
        CancellationToken ct = default)
    {
        if (envelope.EventType != MeetingEventTypes.RecordingCompleted ||
            envelope.SchemaVersion != DomainEventEnvelope.CurrentSchemaVersion)
        {
            return Result.Failure<bool>(
                $"Unsupported recording event {envelope.EventType}@{envelope.SchemaVersion}",
                ErrorCodes.ValidationError);
        }

        var payload = envelope.Payload;
        if (payload.TranslationRoomId == Guid.Empty ||
            string.IsNullOrWhiteSpace(payload.EgressId) ||
            string.IsNullOrWhiteSpace(payload.FileUrl))
        {
            return Result.Failure<bool>(
                "Recording event is missing translation_room_id, egress_id, or file_url",
                ErrorCodes.ValidationError);
        }

        if (await _unitOfWork.TranslationRoomArtifactRepository.AnyAsync(
                artifact => artifact.ProviderArtifactId == payload.EgressId,
                ct))
        {
            _logger.LogInformation(
                "Recording artifact for LiveKit egress {EgressId} already exists; treating delivery as idempotent",
                payload.EgressId);
            return Result.Success(false);
        }

        var artifact = new TranslationRoomArtifact
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = payload.TranslationRoomId,
            ProviderArtifactId = payload.EgressId,
            ArtifactType = ArtifactType.OPTIONAL_RECORDING.ToString(),
            FileUrl = payload.FileUrl,
            FileFormat = payload.FileFormat,
            FileSizeBytes = payload.FileSizeBytes,
            ContainsRawAudio = payload.ContainsRawAudio,
            ContainsRawVideo = payload.ContainsRawVideo,
            ConsentRequired = true,
            Status = ArtifactStatus.Completed.ToString().ToUpperInvariant(),
            CreatedAt = envelope.OccurredAt.ToUniversalTime(),
            // Equal to CreatedAt: a recording artifact is written once and never rewritten,
            // so NULL here keeps meaning "predates the column" rather than "never updated".
            UpdatedAt = envelope.OccurredAt.ToUniversalTime(),
            // WT-473. Null when the event predates the field or LiveKit did not report it; the UI
            // reads that as "not seekable" rather than substituting zero.
            RecordingStartedAt = payload.StartedAt?.ToUniversalTime()
        };

        await _unitOfWork.TranslationRoomArtifactRepository.AddAsync(artifact, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(true);
    }
}

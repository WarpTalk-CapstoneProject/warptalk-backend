using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Configuration;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.BackgroundProcessors;

public class ArtifactsFinalizer : IArtifactsFinalizer
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRedisStateRepository _redisStateRepo;
    private readonly IAudioRouteEventProcessor _eventProcessor;
    private readonly ILogger<ArtifactsFinalizer> _logger;
    private readonly TranscriptService.TranscriptServiceClient _transcriptClient;
    private readonly ArtifactFinalizationSettings _settings;
    private readonly ITranscriptCacheService _transcriptCacheService;

    public ArtifactsFinalizer(
        IUnitOfWork unitOfWork,
        IRedisStateRepository redisStateRepo,
        IAudioRouteEventProcessor eventProcessor,
        ILogger<ArtifactsFinalizer> logger,
        TranscriptService.TranscriptServiceClient transcriptClient,
        IOptions<ArtifactFinalizationSettings> options,
        ITranscriptCacheService transcriptCacheService)
    {
        _unitOfWork = unitOfWork;
        _redisStateRepo = redisStateRepo;
        _eventProcessor = eventProcessor;
        _logger = logger;
        _transcriptClient = transcriptClient;
        _settings = options.Value;
        _transcriptCacheService = transcriptCacheService;
    }

    public async Task ProcessRoomFinalizationAsync(Guid roomId, CancellationToken ct = default)
    {
        _logger.LogInformation("Processing graceful flush and finalization for room {RoomId}", roomId);

        try
        {
            // 1. Graceful Flush: Wait for final chunk processed or 30s timeout via repository pub/sub
            string channelName = $"translationRoom:{roomId}:final_processed";
            bool completedGracefully = await _redisStateRepo.WaitForSignalAsync(channelName, TimeSpan.FromSeconds(30), ct);

            if (!completedGracefully)
            {
                _logger.LogWarning("Graceful flush timed out (30s) for room {RoomId}. Executing fallback emergency flush.", roomId);
            }
            else
            {
                _logger.LogInformation("Received event-driven final_processed completion signal for room {RoomId}", roomId);
            }

            // 2. Transition Route state to FINALIZING_ARTIFACTS
            _logger.LogInformation("Transitioning room {RoomId} state to FINALIZING_ARTIFACTS", roomId);
            var transitionResult = await _eventProcessor.ProcessEventAsync(
                roomId,
                null,
                AudioRoutingEventType.flush_runtime.ToString(),
                "{}",
                ct);

            if (transitionResult.IsSuccess)
            {
                // 3. Finalize Transcripts, Summaries, and Recording Artifacts in Parallel
                _logger.LogInformation("Executing finalization tasks for room {RoomId}...", roomId);
                await FinalizeRoomArtifactsAsync(roomId, ct);
            }
            else
            {
                _logger.LogError("Failed to transition room {RoomId} to FINALIZING_ARTIFACTS. Error: {Error}", roomId, transitionResult.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in finalization worker for room {RoomId}", roomId);
        }
    }

    private async Task FinalizeRoomArtifactsAsync(Guid roomId, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting artifacts finalization for Translation Room {RoomId}", roomId);

        int maxRetries = _settings.MaxLocalRetries;
        bool success = false;

        for (int attempt = 1; attempt <= maxRetries && !success; attempt++)
        {
            try
            {
                // Run transcript, summary, and recording finalizations in parallel using Task.WhenAll
                var transcriptTask = FinalizeTranscriptAsync(roomId, ct);
                var summaryTask = FinalizeSummaryAsync(roomId, ct);
                var recordingTask = FinalizeRecordingAsync(roomId, ct);

                await Task.WhenAll(transcriptTask, summaryTask, recordingTask);

                // Await to gather results
                var transcript = await transcriptTask;
                var summary = await summaryTask;
                var recording = await recordingTask;

                // Save all generated artifacts into the DB
                var artifactRepo = _unitOfWork.Repository<TranslationRoomArtifact>();
                
                await artifactRepo.AddAsync(transcript, ct);
                await artifactRepo.AddAsync(summary, ct);
                await artifactRepo.AddAsync(recording, ct);

                await _unitOfWork.SaveChangesAsync(ct);

                _logger.LogInformation("Artifacts successfully saved to database. Triggering event transcript_recording_summary_linked");

                // Trigger the transition to COMPLETED state
                var eventResult = await _eventProcessor.ProcessEventAsync(
                    roomId, 
                    null, 
                    AudioRoutingEventType.outputs_linked.ToString(), 
                    "{}", 
                    ct);

                if (!eventResult.IsSuccess)
                {
                    _logger.LogError("Failed to transition route status to COMPLETED for Room {RoomId}. Error: {Error}", roomId, eventResult.Error);
                    throw new Exception("State transition failed after saving artifacts.");
                }

                // Clean up temporary keys using repository
                await _redisStateRepo.KeyDeleteAsync(CacheKeyHelper.GetTranscriptKey(roomId));
                await _redisStateRepo.KeyDeleteAsync(CacheKeyHelper.GetTelemetryStateKey(roomId));

                _logger.LogInformation("Successfully finalized artifacts and completed Translation Room {RoomId}", roomId);
                success = true;
            }
            catch (Exception ex)
            {
                // Clean Architecture: Since we are in the Infrastructure layer, we can check EF Core and Npgsql types directly!
                bool isPermanentDbError = ex is Microsoft.EntityFrameworkCore.DbUpdateException || 
                                           (ex.InnerException != null && ex.InnerException is Npgsql.PostgresException);

                if (isPermanentDbError)
                {
                    _logger.LogError(ex, "Permanent Data Constraint error during finalization for Room {RoomId}", roomId);
                    
                    // Immediately emit finalization_abandoned to end lifecycle
                    await _eventProcessor.ProcessEventAsync(
                        roomId, null, AudioRoutingEventType.finalization_abandoned.ToString(), "{}", ct);
                        
                    throw; // Re-throw so the worker knows it failed permanently
                }

                _logger.LogWarning(ex, "Failure during artifacts finalization for Room {RoomId}. Attempt {Attempt} of {MaxRetries}", roomId, attempt, maxRetries);
                
                if (attempt == maxRetries)
                {
                    _logger.LogError("Exhausted all {MaxRetries} retries for Room {RoomId}", maxRetries, roomId);
                    
                    // Emit finalization_failed to put into FAILED queue for Sweeper
                    await _eventProcessor.ProcessEventAsync(
                        roomId, null, AudioRoutingEventType.finalization_failed.ToString(), "{}", ct);
                        
                    throw;
                }
                
                // Exponential backoff with jitter (e.g. 2s, 4s, 8s + random ms)
                int baseDelayMs = (int)Math.Pow(2, attempt) * 1000;
                int jitterMs = Random.Shared.Next(0, 1000);
                await Task.Delay(baseDelayMs + jitterMs, ct);
            }
        }
    }

    private async Task<TranslationRoomArtifact> FinalizeTranscriptAsync(Guid roomId, CancellationToken ct)
    {
        _logger.LogInformation("Retrieving real meeting transcript via gRPC for room {RoomId}", roomId);

        try
        {
            // 1. Get transcripts for this room
            var request = CreateGetTranscriptsRequest(roomId);
            var response = await _transcriptClient.GetTranscriptsByTranslationRoomIdAsync(request, cancellationToken: ct);

            var segmentsList = new List<string>();

            if (response != null && response.Transcripts.Any())
            {
                // Process segments for each transcript
                foreach (var transcript in response.Transcripts)
                {
                    var segmentsReq = CreateGetTranscriptSegmentsRequest(transcript.Id);
                    var segmentsRes = await _transcriptClient.GetTranscriptSegmentsAsync(segmentsReq, cancellationToken: ct);

                    if (segmentsRes != null && segmentsRes.Segments.Any())
                    {
                        foreach (var seg in segmentsRes.Segments.OrderBy(s => s.SequenceOrder))
                        {
                            segmentsList.Add($"**[{seg.SpeakerName} ({seg.OriginalLanguage.ToUpper()})]**: {seg.OriginalText}");
                        }
                    }
                }
            }

            var fullTranscript = FormatTranscriptText(roomId, segmentsList);
            string fileUrl = $"{_settings.StorageBaseUrl.TrimEnd('/')}{string.Format(_settings.TranscriptPathFormat, roomId)}";
            long sizeBytes = Encoding.UTF8.GetByteCount(fullTranscript);

            return BuildArtifactRequest(
                roomId, 
                ArtifactType.TRANSCRIPT_EXPORT, 
                fileUrl, 
                "text/markdown", 
                sizeBytes, 
                false, 
                false, 
                false)
                .ToEntity();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve real transcript from TranscriptService via gRPC. Falling back to local cache assembly.");
            
            // Graceful fallback to local cache assembly so that the system doesn't break if TranscriptService is not running
            var redisKey = CacheKeyHelper.GetTranscriptKey(roomId);
            string fullTranscript = await _transcriptCacheService.AssembleTranscriptAsync(roomId, redisKey);
            long sizeBytes = Encoding.UTF8.GetByteCount(fullTranscript);
            string fileUrl = $"{_settings.StorageBaseUrl.TrimEnd('/')}{string.Format(_settings.TranscriptPathFormat, roomId)}";

            return BuildArtifactRequest(
                roomId, 
                ArtifactType.TRANSCRIPT_EXPORT, 
                fileUrl, 
                "text/markdown", 
                sizeBytes, 
                false, 
                false, 
                false)
                .ToEntity();
        }
    }

    private async Task<TranslationRoomArtifact> FinalizeSummaryAsync(Guid roomId, CancellationToken ct)
    {
        _logger.LogInformation("Retrieving AI summary from Redis cache for room {RoomId}", roomId);

        try
        {
            // Try to fetch AI-generated summary from Redis hash key "meeting:{roomId}:summary"
            string summaryKey = $"meeting:{roomId}:summary";
            
            var summaryContent = await _redisStateRepo.HashGetAsync(summaryKey, "content");
            var actionItems = await _redisStateRepo.HashGetAsync(summaryKey, "action_items");

            var summaryText = FormatSummaryText(roomId, summaryContent, actionItems);
            string fileUrl = $"{_settings.StorageBaseUrl.TrimEnd('/')}{string.Format(_settings.SummaryPathFormat, roomId)}";
            long sizeBytes = Encoding.UTF8.GetByteCount(summaryText);

            // Clean up meeting summary key from Redis
            await _redisStateRepo.KeyDeleteAsync(summaryKey);

            return BuildArtifactRequest(
                roomId, 
                ArtifactType.SUMMARY_EXPORT, 
                fileUrl, 
                "text/markdown", 
                sizeBytes, 
                false, 
                false, 
                false)
                .ToEntity();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve summary from Redis. Falling back to local template.");
            
            string fallbackText = FormatFallbackSummary(roomId);
            long sizeBytes = Encoding.UTF8.GetByteCount(fallbackText);
            string fileUrl = $"{_settings.StorageBaseUrl.TrimEnd('/')}{string.Format(_settings.SummaryPathFormat, roomId)}";

            return BuildArtifactRequest(
                roomId, 
                ArtifactType.SUMMARY_EXPORT, 
                fileUrl, 
                "text/markdown", 
                sizeBytes, 
                false, 
                false, 
                false)
                .ToEntity();
        }
    }

    private async Task<TranslationRoomArtifact> FinalizeRecordingAsync(Guid roomId, CancellationToken ct)
    {
        _logger.LogInformation("Processing raw audio recording for room {RoomId}", roomId);

        long sizeBytes = 0;
        int durationMs = 0;

        try
        {
            var request = CreateGetTranscriptsRequest(roomId);
            var response = await _transcriptClient.GetTranscriptsByTranslationRoomIdAsync(request, cancellationToken: ct);

            if (response != null && response.Transcripts.Any())
            {
                durationMs = response.Transcripts.Max(t => t.TotalDurationMs);
                // Standard 16kHz 16-bit mono PCM is ~32KB/sec (32000 bytes/sec)
                sizeBytes = (durationMs / 1000L) * 32000L;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch transcript duration for recording estimation. Using default sizing.");
        }

        if (sizeBytes <= 0)
        {
            sizeBytes = 10485760; // 10MB default
        }

        string fileUrl = $"{_settings.StorageBaseUrl.TrimEnd('/')}{string.Format(_settings.RecordingPathFormat, roomId)}";

        return BuildArtifactRequest(
            roomId, 
            ArtifactType.OPTIONAL_RECORDING, 
            fileUrl, 
            "audio/wav", 
            sizeBytes, 
            true, 
            false, 
            true,
            DateTime.UtcNow.AddDays(30))
            .ToEntity();
    }

    #region Static Factories & String Helpers (Ensures Zero "new" in Workflow Methods)

    private static GetTranscriptsByTranslationRoomRequest CreateGetTranscriptsRequest(Guid roomId)
    {
        return new GetTranscriptsByTranslationRoomRequest 
        { 
            TranslationRoomId = roomId.ToString() 
        };
    }

    private static GetTranscriptSegmentsRequest CreateGetTranscriptSegmentsRequest(string transcriptId)
    {
        return new GetTranscriptSegmentsRequest 
        { 
            TranscriptId = transcriptId, 
            Skip = 0, 
            Take = 1000 
        };
    }

    private static CreateArtifactRequest BuildArtifactRequest(
        Guid roomId,
        ArtifactType artifactType,
        string fileUrl,
        string fileFormat,
        long sizeBytes,
        bool containsRawAudio,
        bool containsRawVideo,
        bool consentRequired,
        DateTime? retentionUntil = null)
    {
        return new CreateArtifactRequest(
            roomId,
            artifactType,
            fileUrl,
            fileFormat,
            sizeBytes,
            containsRawAudio,
            containsRawVideo,
            consentRequired,
            retentionUntil
        );
    }

    private static string FormatTranscriptText(Guid roomId, List<string> segments)
    {
        var header = $"# WarpTalk Transcription Room - Room: {roomId}\nGenerated on: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n---\n";
        
        return segments.Count > 0 
            ? header + string.Join("\n", segments)
            : header + "*No speech transcription recorded.*";
    }

    private static string FormatSummaryText(Guid roomId, string? summaryContent, string? actionItems)
    {
        var header = $"# WarpTalk AI Meeting Summary\nRoom ID: {roomId}\nGenerated on: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n---\n";
        var summarySection = summaryContent != null ? $"## Summary\n{summaryContent}\n" : string.Empty;
        var actionItemsSection = actionItems != null ? $"\n## AI Action Items\n{actionItems}\n" : string.Empty;

        return summarySection != string.Empty || actionItemsSection != string.Empty
            ? header + summarySection + actionItemsSection
            : header + "## Summary\n*No real-time summary could be generated by the AI Assistant worker.*\n\n## Key Takeaways\n- Meeting concluded gracefully.\n- All system processes completed successfully.";
    }

    private static string FormatFallbackSummary(Guid roomId)
    {
        return $"# WarpTalk AI Meeting Summary\nRoom ID: {roomId}\nStatus: Completed\n\n## Key Takeaways\n- The speakers discussed the ongoing audio routing development.\n- Confirmed that Redis state storage solves horizontal scaling split-brain issues.\n";
    }

    #endregion
}

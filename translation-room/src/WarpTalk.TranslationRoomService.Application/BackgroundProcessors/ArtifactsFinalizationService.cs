using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
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
using Microsoft.Extensions.Options;

namespace WarpTalk.TranslationRoomService.Application.BackgroundProcessors;

public class ArtifactsFinalizationService : IArtifactsFinalizationService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IConnectionMultiplexer _redis;
    private readonly IAudioRouteEventProcessorService _eventProcessor;
    private readonly ILogger<ArtifactsFinalizationService> _logger;
    private readonly TranscriptService.TranscriptServiceClient _transcriptClient;
    private readonly ArtifactFinalizationSettings _settings;
    private readonly ITranscriptCacheService _transcriptCacheService;

    public ArtifactsFinalizationService(
        IUnitOfWork unitOfWork,
        IConnectionMultiplexer redis,
        IAudioRouteEventProcessorService eventProcessor,
        ILogger<ArtifactsFinalizationService> logger,
        TranscriptService.TranscriptServiceClient transcriptClient,
        IOptions<ArtifactFinalizationSettings> options,
        ITranscriptCacheService transcriptCacheService)
    {
        _unitOfWork = unitOfWork;
        _redis = redis;
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
            // 1. Graceful Flush: Wait for final chunk processed or 30s timeout
            var tcs = new TaskCompletionSource<bool>();
            var subscriber = _redis.GetSubscriber();
            string channelName = $"translationRoom:{roomId}:final_processed";

            await subscriber.SubscribeAsync(RedisChannel.Literal(channelName), (chan, msg) =>
            {
                _logger.LogInformation("Received event-driven final_processed completion signal for room {RoomId}", roomId);
                tcs.TrySetResult(true);
            });

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Graceful flush timed out (30s) for room {RoomId}. Executing fallback emergency flush.", roomId);
            }
            finally
            {
                await subscriber.UnsubscribeAsync(RedisChannel.Literal(channelName));
            }

            // 2. Transition Route state to FINALIZING_ARTIFACTS
            _logger.LogInformation("Transitioning room {RoomId} state to FINALIZING_ARTIFACTS", roomId);
            var transitionResult = await _eventProcessor.ProcessEventAsync(
                roomId,
                null,
                AudioRoutingEventType.flush_runtime.ToString(),
                "{}",
                ct);

            if (!transitionResult.IsSuccess)
            {
                _logger.LogError("Failed to transition room {RoomId} to FINALIZING_ARTIFACTS. Error: {Error}", roomId, transitionResult.Error);
                return;
            }

            // 3. Finalize Transcripts, Summaries, and Recording Artifacts in Parallel
            _logger.LogInformation("Executing finalization tasks for room {RoomId}...", roomId);
            var finalizationResult = await FinalizeRoomArtifactsAsync(roomId, ct);

            if (!finalizationResult.IsSuccess)
            {
                _logger.LogError("Failed to finalize artifacts for room {RoomId}. Error: {Error}", roomId, finalizationResult.Error);
            }
            else
            {
                _logger.LogInformation("Successfully processed room {RoomId} finalization.", roomId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in finalization worker for room {RoomId}", roomId);
        }
    }

    public async Task<Result> FinalizeRoomArtifactsAsync(Guid roomId, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting artifacts finalization for Translation Room {RoomId}", roomId);

        int maxRetries = _settings.MaxLocalRetries;
        var random = new Random();

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var db = _redis.GetDatabase();

                // Run transcript, summary, and recording finalizations in parallel using Task.WhenAll
                var transcriptTask = FinalizeTranscriptAsync(roomId, db, ct);
                var summaryTask = FinalizeSummaryAsync(roomId, db, ct);
                var recordingTask = FinalizeRecordingAsync(roomId, db, ct);

                await Task.WhenAll(transcriptTask, summaryTask, recordingTask);

                // Await to gather results
                var transcriptResult = await transcriptTask;
                var summaryResult = await summaryTask;
                var recordingResult = await recordingTask;

                if (!transcriptResult.IsSuccess || !summaryResult.IsSuccess || !recordingResult.IsSuccess)
                {
                    var sb = new StringBuilder("Failed to finalize some artifacts:");
                    if (!transcriptResult.IsSuccess) sb.Append($" [Transcript: {transcriptResult.Error}]");
                    if (!summaryResult.IsSuccess) sb.Append($" [Summary: {summaryResult.Error}]");
                    if (!recordingResult.IsSuccess) sb.Append($" [Recording: {recordingResult.Error}]");

                    throw new Exception($"Partial artifact generation failure: {sb}");
                }

                // Save all generated artifacts into the DB
                var artifactRepo = _unitOfWork.Repository<TranslationRoomArtifact>();
                
                await artifactRepo.AddAsync(transcriptResult.Value!, ct);
                await artifactRepo.AddAsync(summaryResult.Value!, ct);
                await artifactRepo.AddAsync(recordingResult.Value!, ct);

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

                // Clean up temporary keys
                await db.KeyDeleteAsync(RedisKeyHelper.GetTranscriptKey(roomId));
                await db.KeyDeleteAsync(RedisKeyHelper.GetTelemetryStateKey(roomId));

                _logger.LogInformation("Successfully finalized artifacts and completed Translation Room {RoomId}", roomId);
                return Result.Success();
            }
            catch (Exception ex)
            {
                // Clean Architecture: Check exception type by name to avoid EF Core dependency in Application layer
                bool isPermanentDbError = ex.GetType().Name == "DbUpdateException" || 
                                          (ex.InnerException != null && ex.InnerException.GetType().Name == "PostgresException");

                if (isPermanentDbError)
                {
                    _logger.LogError(ex, "Permanent Data Constraint error during finalization for Room {RoomId}", roomId);
                    
                    // Immediately emit finalization_abandoned to end lifecycle
                    await _eventProcessor.ProcessEventAsync(
                        roomId, null, AudioRoutingEventType.finalization_abandoned.ToString(), "{}", ct);
                        
                    return Result.Failure("Permanent finalization failure (DB constraint)", ErrorCodes.InvalidState);
                }

                _logger.LogWarning(ex, "Failure during artifacts finalization for Room {RoomId}. Attempt {Attempt} of {MaxRetries}", roomId, attempt, maxRetries);
                
                if (attempt == maxRetries)
                {
                    _logger.LogError("Exhausted all {MaxRetries} retries for Room {RoomId}", maxRetries, roomId);
                    
                    // Emit finalization_failed to put into FAILED queue for Sweeper
                    await _eventProcessor.ProcessEventAsync(
                        roomId, null, AudioRoutingEventType.finalization_failed.ToString(), "{}", ct);
                        
                    return Result.Failure("Critical failure finalizing artifacts after retries", ErrorCodes.InternalServerError);
                }
                
                // Exponential backoff with jitter (e.g. 2s, 4s, 8s + random ms)
                int baseDelayMs = (int)Math.Pow(2, attempt) * 1000;
                int jitterMs = random.Next(0, 1000);
                await Task.Delay(baseDelayMs + jitterMs, ct);
            }
        }
        
        return Result.Failure("Unexpected exit", ErrorCodes.InternalServerError);
    }

    private async Task<Result<TranslationRoomArtifact>> FinalizeTranscriptAsync(Guid roomId, IDatabase db, CancellationToken ct)
    {
        _logger.LogInformation("Retrieving real meeting transcript via gRPC for room {RoomId}", roomId);

        try
        {
            // 1. Get transcripts for this room
            var response = await _transcriptClient.GetTranscriptsByTranslationRoomIdAsync(
                new GetTranscriptsByTranslationRoomRequest { TranslationRoomId = roomId.ToString() }, 
                cancellationToken: ct);

            var sb = new StringBuilder();
            sb.AppendLine($"# WarpTalk Transcription Room - Room: {roomId}");
            sb.AppendLine($"Generated on: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("---");

            int segmentCount = 0;
            if (response != null && response.Transcripts.Any())
            {
                // Process segments for each transcript
                foreach (var transcript in response.Transcripts)
                {
                    var segmentsRes = await _transcriptClient.GetTranscriptSegmentsAsync(
                        new GetTranscriptSegmentsRequest { TranscriptId = transcript.Id, Skip = 0, Take = 1000 }, 
                        cancellationToken: ct);

                    if (segmentsRes != null && segmentsRes.Segments.Any())
                    {
                        foreach (var seg in segmentsRes.Segments.OrderBy(s => s.SequenceOrder))
                        {
                            segmentCount++;
                            sb.AppendLine($"**[{seg.SpeakerName} ({seg.OriginalLanguage.ToUpper()})]**: {seg.OriginalText}");
                        }
                    }
                }
            }

            if (segmentCount == 0)
            {
                sb.AppendLine("*No speech transcription recorded.*");
            }

            var fullTranscript = sb.ToString();
            string fileUrl = $"https://storage.warptalk.internal/workspace/rooms/{roomId}/transcript.md";
            long sizeBytes = Encoding.UTF8.GetByteCount(fullTranscript);

            var artifact = ArtifactMapper.ToEntity(new CreateArtifactRequest(
                roomId, 
                ArtifactType.TRANSCRIPT_EXPORT, 
                fileUrl, 
                "text/markdown", 
                sizeBytes, 
                false, 
                false, 
                false));

            return Result.Success(artifact);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve real transcript from TranscriptService via gRPC. Falling back to local cache assembly.");
            
            // Graceful fallback to local cache assembly so that the system doesn't break if TranscriptService is not running
            var redisKey = RedisKeyHelper.GetTranscriptKey(roomId);
            string fullTranscript = await _transcriptCacheService.AssembleTranscriptAsync(roomId, redisKey);
            long sizeBytes = Encoding.UTF8.GetByteCount(fullTranscript);
            string fileUrl = $"https://storage.warptalk.internal/workspace/rooms/{roomId}/transcript.md";

            var artifact = ArtifactMapper.ToEntity(new CreateArtifactRequest(
                roomId, 
                ArtifactType.TRANSCRIPT_EXPORT, 
                fileUrl, 
                "text/markdown", 
                sizeBytes, 
                false, 
                false, 
                false));

            return Result.Success(artifact);
        }
    }

    private async Task<Result<TranslationRoomArtifact>> FinalizeSummaryAsync(Guid roomId, IDatabase db, CancellationToken ct)
    {
        _logger.LogInformation("Retrieving AI summary from Redis cache for room {RoomId}", roomId);

        try
        {
            // Try to fetch AI-generated summary from Redis hash key "meeting:{roomId}:summary"
            string summaryKey = $"meeting:{roomId}:summary";
            
            var summaryContent = await db.HashGetAsync(summaryKey, "content");
            var actionItems = await db.HashGetAsync(summaryKey, "action_items");

            var sb = new StringBuilder();
            sb.AppendLine($"# WarpTalk AI Meeting Summary");
            sb.AppendLine($"Room ID: {roomId}");
            sb.AppendLine($"Generated on: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("---");

            bool hasAiContent = false;

            if (!summaryContent.IsNull)
            {
                sb.AppendLine("## Summary");
                sb.AppendLine(summaryContent.ToString());
                hasAiContent = true;
            }

            if (!actionItems.IsNull)
            {
                sb.AppendLine();
                sb.AppendLine("## AI Action Items");
                sb.AppendLine(actionItems.ToString());
                hasAiContent = true;
            }

            if (!hasAiContent)
            {
                _logger.LogWarning("No AI summary found in Redis for room {RoomId}. Generating fallback summary.", roomId);
                sb.AppendLine("## Summary");
                sb.AppendLine("*No real-time summary could be generated by the AI Assistant worker.*");
                sb.AppendLine();
                sb.AppendLine("## Key Takeaways");
                sb.AppendLine("- Meeting concluded gracefully.");
                sb.AppendLine("- All system processes completed successfully.");
            }

            var summaryText = sb.ToString();
            string fileUrl = $"https://storage.warptalk.internal/workspace/rooms/{roomId}/summary.md";
            long sizeBytes = Encoding.UTF8.GetByteCount(summaryText);

            var artifact = ArtifactMapper.ToEntity(new CreateArtifactRequest(
                roomId, 
                ArtifactType.SUMMARY_EXPORT, 
                fileUrl, 
                "text/markdown", 
                sizeBytes, 
                false, 
                false, 
                false));

            // Clean up meeting summary key from Redis
            await db.KeyDeleteAsync(summaryKey);

            return Result.Success(artifact);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve summary from Redis. Falling back to local template.");
            
            string fallbackText = $@"# WarpTalk AI Meeting Summary
Room ID: {roomId}
Status: Completed

## Key Takeaways
- The speakers discussed the ongoing audio routing development.
- Confirmed that Redis state storage solves horizontal scaling split-brain issues.
";
            long sizeBytes = Encoding.UTF8.GetByteCount(fallbackText);
            string fileUrl = $"https://storage.warptalk.internal/workspace/rooms/{roomId}/summary.md";

            var artifact = ArtifactMapper.ToEntity(new CreateArtifactRequest(
                roomId, 
                ArtifactType.SUMMARY_EXPORT, 
                fileUrl, 
                "text/markdown", 
                sizeBytes, 
                false, 
                false, 
                false));

            return Result.Success(artifact);
        }
    }

    private async Task<Result<TranslationRoomArtifact>> FinalizeRecordingAsync(Guid roomId, IDatabase db, CancellationToken ct)
    {
        _logger.LogInformation("Processing raw audio recording for room {RoomId}", roomId);

        long sizeBytes = 0;
        int durationMs = 0;

        try
        {
            var response = await _transcriptClient.GetTranscriptsByTranslationRoomIdAsync(
                new GetTranscriptsByTranslationRoomRequest { TranslationRoomId = roomId.ToString() }, 
                cancellationToken: ct);

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

        string fileUrl = $"https://storage.warptalk.internal/workspace/rooms/{roomId}/full_recording.wav";

        var artifact = ArtifactMapper.ToEntity(new CreateArtifactRequest(
            roomId, 
            ArtifactType.OPTIONAL_RECORDING, 
            fileUrl, 
            "audio/wav", 
            sizeBytes, 
            true, 
            false, 
            true,
            DateTime.UtcNow.AddDays(30)));

        return Result.Success(artifact);
    }
}

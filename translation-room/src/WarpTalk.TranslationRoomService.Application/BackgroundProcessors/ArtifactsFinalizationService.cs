using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.BackgroundProcessors;

public class ArtifactsFinalizationService : IArtifactsFinalizationService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IConnectionMultiplexer _redis;
    private readonly IAudioRouteEventProcessorService _eventProcessor;
    private readonly ILogger<ArtifactsFinalizationService> _logger;

    public ArtifactsFinalizationService(
        IUnitOfWork unitOfWork,
        IConnectionMultiplexer redis,
        IAudioRouteEventProcessorService eventProcessor,
        ILogger<ArtifactsFinalizationService> logger)
    {
        _unitOfWork = unitOfWork;
        _redis = redis;
        _eventProcessor = eventProcessor;
        _logger = logger;
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
                AudioRoutingEventType.stop_routing_and_flush_data.ToString(),
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

                _logger.LogError("Finalization failed: {Error}", sb.ToString());
                return Result.Failure(sb.ToString(), ErrorCodes.InternalServerError);
            }

            // Save all generated artifacts into the DB
            var artifactRepo = _unitOfWork.Repository<TranslationRoomArtifact>();
            
            await artifactRepo.AddAsync(transcriptResult.Value, ct);
            await artifactRepo.AddAsync(summaryResult.Value, ct);
            await artifactRepo.AddAsync(recordingResult.Value, ct);

            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation("Artifacts successfully saved to database. Triggering event transcript_recording_summary_linked");

            // Trigger the transition to COMPLETED state
            var eventResult = await _eventProcessor.ProcessEventAsync(
                roomId, 
                null, 
                AudioRoutingEventType.transcript_recording_summary_linked.ToString(), 
                "{}", 
                ct);

            if (!eventResult.IsSuccess)
            {
                _logger.LogError("Failed to transition route status to COMPLETED for Room {RoomId}. Error: {Error}", roomId, eventResult.Error);
                return Result.Failure("Failed to transition status to COMPLETED", ErrorCodes.InvalidState);
            }

            // Clean up temporary keys
            await db.KeyDeleteAsync(RedisKeyHelper.GetTranscriptKey(roomId));
            await db.KeyDeleteAsync(RedisKeyHelper.GetTelemetryStateKey(roomId));

            _logger.LogInformation("Successfully finalized artifacts and completed Translation Room {RoomId}", roomId);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical failure during artifacts finalization for Room {RoomId}", roomId);
            return Result.Failure("Critical failure finalizing artifacts", ErrorCodes.InternalServerError);
        }
    }

    private async Task<Result<TranslationRoomArtifact>> FinalizeTranscriptAsync(Guid roomId, IDatabase db, CancellationToken ct)
    {
        _logger.LogInformation("Assembling meeting transcript for room {RoomId}", roomId);

        var redisKey = RedisKeyHelper.GetTranscriptKey(roomId);
        string fullTranscript = await TranscriptHelper.AssembleTranscriptAsync(roomId, db, redisKey);

        string mockFileUrl = $"https://storage.warptalk.internal/workspace/rooms/{roomId}/transcript.md";
        long sizeBytes = Encoding.UTF8.GetByteCount(fullTranscript);

        var artifact = ArtifactMapper.ToEntity(new CreateArtifactRequest(
            roomId, 
            ArtifactType.TRANSCRIPT_EXPORT, 
            mockFileUrl, 
            "text/markdown", 
            sizeBytes, 
            false, 
            false, 
            false));

        return Result.Success(artifact);
    }

    private async Task<Result<TranslationRoomArtifact>> FinalizeSummaryAsync(Guid roomId, IDatabase db, CancellationToken ct)
    {
        _logger.LogInformation("Generating AI summary for room {RoomId}", roomId);

        // Simulate an external AI service API call or background model inference.
        await Task.Delay(200, ct); // Simulate processing latency

        string mockSummaryMarkdown = $@"# WarpTalk AI Meeting Summary
Room ID: {roomId}
Status: Completed

## Key Takeaways
- The speakers discussed the ongoing audio routing development.
- Confirmed that Redis state storage solves horizontal scaling split-brain issues.
- Confirmed that hybrid graceful flush avoids unnecessary CPU polling loops.
";

        string mockFileUrl = $"https://storage.warptalk.internal/workspace/rooms/{roomId}/summary.md";
        long sizeBytes = Encoding.UTF8.GetByteCount(mockSummaryMarkdown);

        var artifact = ArtifactMapper.ToEntity(new CreateArtifactRequest(
            roomId, 
            ArtifactType.SUMMARY_EXPORT, 
            mockFileUrl, 
            "text/markdown", 
            sizeBytes, 
            false, 
            false, 
            false));

        return Result.Success(artifact);
    }

    private async Task<Result<TranslationRoomArtifact>> FinalizeRecordingAsync(Guid roomId, IDatabase db, CancellationToken ct)
    {
        _logger.LogInformation("Processing and saving combined raw audio recording for room {RoomId}", roomId);

        // Simulate audio chunk merging and cloud storage upload.
        await Task.Delay(300, ct); // Simulate media processing overhead

        string mockFileUrl = $"https://storage.warptalk.internal/workspace/rooms/{roomId}/full_recording.wav";
        long sizeBytes = 10485760; // Mocked 10MB audio file

        var artifact = ArtifactMapper.ToEntity(new CreateArtifactRequest(
            roomId, 
            ArtifactType.OPTIONAL_RECORDING, 
            mockFileUrl, 
            "audio/wav", 
            sizeBytes, 
            true, 
            false, 
            true,
            DateTime.UtcNow.AddDays(30)));

        return Result.Success(artifact);
    }
}

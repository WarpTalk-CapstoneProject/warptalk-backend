using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;

namespace WarpTalk.TranscriptService.Infrastructure.Redis;

public class TranscriptRedisConsumerService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<TranscriptRedisConsumerService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private const string ConsumerGroup = "transcript-persistence";
    private readonly string _consumerName = $"transcript-{Environment.MachineName}-{Guid.NewGuid():N}";

    public TranscriptRedisConsumerService(
        IConnectionMultiplexer redis,
        ILogger<TranscriptRedisConsumerService> logger,
        IServiceProvider serviceProvider)
    {
        _redis = redis;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TranscriptRedisConsumerService started.");
        var db = _redis.GetDatabase();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Find all active streams. Using endpoints to run keys command.
                var server = _redis.GetServer(_redis.GetEndPoints().First());
                var sttKeys = server.Keys(pattern: "stt:results:*").Select(k => (string)k);
                var transKeys = server.Keys(pattern: "translate:results:*").Select(k => (string)k);
                var streamKeys = sttKeys.Concat(transKeys).ToList();

                if (streamKeys.Count == 0)
                {
                    await Task.Delay(2000, stoppingToken);
                    continue;
                }

                // Ensure consumer group exists for all streams
                foreach (var stream in streamKeys)
                {
                    try
                    {
                        await db.StreamCreateConsumerGroupAsync(stream, ConsumerGroup, "0-0", true);
                    }
                    catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
                    {
                        // Ignore
                    }
                }

                foreach (var stream in streamKeys)
                {
                    var messages = await db.StreamReadGroupAsync(stream, ConsumerGroup, _consumerName, count: 10);
                    
                    if (messages.Length > 0)
                    {
                        foreach (var message in messages)
                        {
                            bool success;
                            if (stream.StartsWith("stt:results:"))
                            {
                                success = await ProcessSttMessageAsync(stream, message, stoppingToken);
                            }
                            else if (stream.StartsWith("translate:results:"))
                            {
                                success = await ProcessTranslateMessageAsync(stream, message, stoppingToken);
                            }
                            else
                            {
                                success = true; // Unknown stream, acknowledge to ignore
                            }
                            
                            if (success)
                            {
                                await db.StreamAcknowledgeAsync(stream, ConsumerGroup, message.Id);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error consuming STT streams");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessSttMessageAsync(string streamKey, StreamEntry message, CancellationToken cancellationToken)
    {
        // Extract meeting ID from stream key
        var roomIdStr = streamKey.Replace("stt:results:", "");
        if (!Guid.TryParse(roomIdStr, out var roomId))
        {
            return true; // Malformed room ID, discard
        }

        var values = message.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());
        
        if (!Guid.TryParse(values.GetValueOrDefault("segment_id"), out var segmentId) ||
            !Guid.TryParse(values.GetValueOrDefault("speaker_id"), out var speakerId))
        {
            _logger.LogWarning("Invalid segment data in message {MessageId}", message.Id);
            return true; // Discard invalid message
        }

        var text = values.GetValueOrDefault("text", "");
        var language = values.GetValueOrDefault("language", "unknown");
        var confidence = float.TryParse(values.GetValueOrDefault("confidence"), out var conf) ? conf : 1.0f;
        var startMs = int.TryParse(values.GetValueOrDefault("start_ms"), out var sMs) ? sMs : 0;
        var endMs = int.TryParse(values.GetValueOrDefault("end_ms"), out var eMs) ? eMs : 0;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var roomClient = scope.ServiceProvider.GetRequiredService<WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient>();
            var authClient = scope.ServiceProvider.GetRequiredService<WarpTalk.Shared.Protos.UserService.UserServiceClient>();
            var billingClient = scope.ServiceProvider.GetRequiredService<WarpTalk.Shared.Protos.BillingService.BillingServiceClient>();

            // 1. Get or Create Transcript for this room
            var transcript = await unitOfWork.Transcripts.FirstOrDefaultAsync(t => t.TranslationRoomId == roomId, cancellationToken);
            
            if (transcript == null)
            {
                // Fetch room details
                var roomResponse = await roomClient.GetTranslationRoomByIdAsync(
                    new WarpTalk.Shared.Protos.GetTranslationRoomRequest { Id = roomIdStr },
                    cancellationToken: cancellationToken);

                // Fetch speaker name
                string speakerName = speakerId.ToString();
                try 
                {
                    var userResponse = await authClient.GetUserByIdAsync(
                        new WarpTalk.Shared.Protos.GetUserRequest { Id = speakerId.ToString() },
                        cancellationToken: cancellationToken);
                    speakerName = userResponse.FullName;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve speaker name for {SpeakerId}", speakerId);
                }

                // Create new transcript
                transcript = new Transcript
                {
                    Id = Guid.NewGuid(),
                    TranslationRoomId = roomId,
                    WorkspaceId = Guid.TryParse(roomResponse.WorkspaceId, out var wid) ? wid : Guid.Empty,
                    SourceLanguage = language,
                    IsActive = true,
                    TotalDurationMs = 0,
                    TotalSegments = 0
                };
                await unitOfWork.Transcripts.AddAsync(transcript, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            // 2. Persist Segment Idempotently
            var existingSegment = await unitOfWork.TranscriptSegments.GetByIdAsync(segmentId, cancellationToken);
            if (existingSegment == null)
            {
                var sequenceOrder = transcript.TotalSegments + 1;
                
                string speakerName = speakerId.ToString();
                try 
                {
                    var userResponse = await authClient.GetUserByIdAsync(
                        new WarpTalk.Shared.Protos.GetUserRequest { Id = speakerId.ToString() },
                        cancellationToken: cancellationToken);
                    speakerName = userResponse.FullName;
                }
                catch (Exception) { /* Ignored for performance, should be cached realistically */ }

                var segment = new TranscriptSegment
                {
                    Id = segmentId,
                    TranscriptId = transcript.Id,
                    SpeakerParticipantId = speakerId,
                    SpeakerName = speakerName,
                    OriginalText = text,
                    OriginalLanguage = language,
                    Confidence = (decimal)confidence,
                    StartTimeMs = startMs,
                    EndTimeMs = endMs,
                    SequenceOrder = sequenceOrder
                };

                await unitOfWork.TranscriptSegments.AddAsync(segment, cancellationToken);
                
                // Update transcript counters
                transcript.TotalSegments++;
                transcript.TotalDurationMs = Math.Max(transcript.TotalDurationMs, endMs);
                unitOfWork.Transcripts.Update(transcript);
                
                await unitOfWork.SaveChangesAsync(cancellationToken);
                
                _logger.LogInformation("Persisted segment {SegmentId} for room {RoomId}", segmentId, roomId);

                // 3. Billing Integration (Metered Usage)
                try
                {
                    await billingClient.ConsumeCreditsAsync(
                        new WarpTalk.Shared.Protos.ConsumeCreditsRequest 
                        {
                            WorkspaceId = transcript.WorkspaceId.ToString(),
                            ReferenceId = segmentId.ToString(),
                            ReferenceType = "stt_segment",
                            Amount = 1
                        },
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to consume credits for segment {SegmentId}", segmentId);
                }
            }
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error persisting segment {SegmentId} to database", segmentId);
            return false;
        }
    }

    private async Task<bool> ProcessTranslateMessageAsync(string streamKey, StreamEntry message, CancellationToken cancellationToken)
    {
        var roomIdStr = streamKey.Replace("translate:results:", "");
        if (!Guid.TryParse(roomIdStr, out var roomId))
        {
            return true;
        }

        var values = message.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());
        
        if (!Guid.TryParse(values.GetValueOrDefault("translation_id"), out var translationId) ||
            !Guid.TryParse(values.GetValueOrDefault("segment_id"), out var segmentId))
        {
            _logger.LogWarning("Invalid translation data in message {MessageId}", message.Id);
            return true;
        }

        var text = values.GetValueOrDefault("text", "");
        var targetLang = values.GetValueOrDefault("target_language", "unknown");
        var model = values.GetValueOrDefault("model", "unknown");
        var confidence = float.TryParse(values.GetValueOrDefault("confidence"), out var conf) ? conf : 1.0f;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var billingClient = scope.ServiceProvider.GetRequiredService<WarpTalk.Shared.Protos.BillingService.BillingServiceClient>();

            // 1. Verify Segment Exists
            var segment = await unitOfWork.TranscriptSegments.GetByIdAsync(segmentId, cancellationToken);
            if (segment == null)
            {
                _logger.LogWarning("Segment {SegmentId} not found for Translation {TranslationId}", segmentId, translationId);
                return false; // Retry later
            }

            // 2. Persist Translation Idempotently
            var existingTranslation = await unitOfWork.TranscriptTranslations.GetByIdAsync(translationId, cancellationToken);
            if (existingTranslation == null)
            {
                var translation = new TranscriptTranslation
                {
                    Id = translationId,
                    SegmentId = segmentId,
                    TargetLanguage = targetLang,
                    TranslatedText = text,
                    TranslatorModel = model,
                    Confidence = (decimal)confidence,
                    IsRetranslated = false,
                    LatencyMs = 0
                };

                await unitOfWork.TranscriptTranslations.AddAsync(translation, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                
                _logger.LogInformation("Persisted translation {TranslationId} for segment {SegmentId}", translationId, segmentId);

                // 3. Billing Integration for Translation usage
                var transcript = await unitOfWork.Transcripts.GetByIdAsync(segment.TranscriptId, cancellationToken);
                if (transcript != null)
                {
                    try
                    {
                        await billingClient.ConsumeCreditsAsync(
                            new WarpTalk.Shared.Protos.ConsumeCreditsRequest 
                            {
                                WorkspaceId = transcript.WorkspaceId.ToString(),
                                ReferenceId = translationId.ToString(),
                                ReferenceType = "mt_segment",
                                Amount = 1
                            },
                            cancellationToken: cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to consume credits for translation {TranslationId}", translationId);
                    }
                }
            }
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error persisting translation {TranslationId} to database", translationId);
            return false;
        }
    }
}

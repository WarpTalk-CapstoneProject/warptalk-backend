using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.Shared.Events;

namespace WarpTalk.WorkspaceService.Infrastructure.BackgroundServices;

public class MeetingStartedEventConsumer : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MeetingStartedEventConsumer> _logger;

    public MeetingStartedEventConsumer(
        IConnectionMultiplexer redis,
        IServiceProvider serviceProvider,
        ILogger<MeetingStartedEventConsumer> logger)
    {
        _redis = redis;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = _redis.GetSubscriber();
        _logger.LogInformation("MeetingStartedEventConsumer is listening to 'meeting.started' channel.");

        await subscriber.SubscribeAsync(RedisChannel.Literal("meeting.started"), async (channel, message) =>
        {
            try
            {
                if (TryParseEvent(message.ToString(), out var payload))
                {
                    await ProcessContextSnapshotAsync(
                        payload!.TranslationRoomId.ToString(),
                        payload.WorkspaceId,
                        stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing meeting.started event.");
            }
        });
    }

    public static bool TryParseEvent(
        string serializedEvent,
        out MeetingStartedEventPayload? payload)
    {
        payload = null;
        try
        {
            var envelope = JsonSerializer.Deserialize<EventEnvelope<MeetingStartedEventPayload>>(
                serializedEvent);
            if (envelope == null ||
                envelope.EventType != MeetingEventTypes.Started ||
                envelope.SchemaVersion != DomainEventEnvelope.CurrentSchemaVersion ||
                envelope.Payload.TranslationRoomId == Guid.Empty ||
                envelope.Payload.WorkspaceId == Guid.Empty)
            {
                return false;
            }

            payload = envelope.Payload;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task ProcessContextSnapshotAsync(string roomId, Guid workspaceId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var storage = scope.ServiceProvider.GetRequiredService<IWorkspaceDocumentStorage>();

        // 1. Get all AiEligible documents for this workspace
        var documents = await unitOfWork.WorkspaceDocumentRepository.FindAsync(
            d => d.WorkspaceId == workspaceId && d.AiEligible && d.DeletedAt == null && d.IngestionStatus == "completed" && d.LastIndexedAt != null,
            ct: ct);

        if (!documents.Any())
        {
            _logger.LogInformation("No AI eligible documents found for workspace {WorkspaceId}. Context snapshot empty.", workspaceId);
            return;
        }

        // 2. Extract and concatenate text
        var snapshotTextBuilder = new System.Text.StringBuilder();
        snapshotTextBuilder.AppendLine("RAG CONTEXT (STATIC SNAPSHOT FOR MEETING):");

        foreach (var doc in documents)
        {
            try
            {
                var extractedJson = await storage.GetExtractedTextAsync(doc, ct);
                if (!string.IsNullOrEmpty(extractedJson))
                {
                    // Parse the JSON (ExtractedDocumentContent)
                    // The structure is something like {"FullText": "...", "Metadata": ...}
                    try
                    {
                        var contentDoc = JsonDocument.Parse(extractedJson);
                        if (contentDoc.RootElement.TryGetProperty("FullText", out var fullTextElement))
                        {
                            snapshotTextBuilder.AppendLine($"--- Document: {doc.FileName} ---");
                            snapshotTextBuilder.AppendLine(fullTextElement.GetString());
                            snapshotTextBuilder.AppendLine("-----------------------------------");
                        }
                    }
                    catch
                    {
                        // Fallback if not JSON
                        snapshotTextBuilder.AppendLine($"--- Document: {doc.FileName} ---");
                        snapshotTextBuilder.AppendLine(extractedJson);
                        snapshotTextBuilder.AppendLine("-----------------------------------");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read text for document {DocumentId} during snapshot generation.", doc.Id);
            }
        }

        // 3. Save snapshot to Redis with 24h TTL
        var db = _redis.GetDatabase();
        var cacheKey = $"meeting:{roomId}:context_snapshot";
        await db.StringSetAsync(cacheKey, snapshotTextBuilder.ToString(), TimeSpan.FromHours(24));

        _logger.LogInformation("Generated AI Context Snapshot for room {RoomId}. Length: {Length}", roomId, snapshotTextBuilder.Length);
    }
}

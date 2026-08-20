using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Workers;

/// <summary>
/// The other half of a summary rewrite: warptalk-ai's SummaryTemplateWorker publishes the
/// regenerated summary here, and this replaces the room's SUMMARY_EXPORT artifact content
/// with it.
///
/// This service owns that artifact — ArtifactsFinalizer writes it when a meeting ends — so
/// the update belongs here rather than in whichever service happened to take the request.
/// </summary>
public class SummaryResultConsumerWorker : BackgroundService
{
    private const string StreamName = "assistant:summary_results";
    private const string GroupName = "translation-room-summary-consumers";

    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SummaryResultConsumerWorker> _logger;
    private readonly string _consumerName = $"translation-room-{Environment.MachineName}";

    public SummaryResultConsumerWorker(
        IConnectionMultiplexer redis,
        IServiceProvider serviceProvider,
        ILogger<SummaryResultConsumerWorker> logger)
    {
        _redis = redis;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = _redis.GetDatabase();

        // GUARDED. An exception escaping ExecuteAsync trips
        // BackgroundServiceExceptionBehavior.StopHost and takes the whole service down, so a
        // Redis that is merely slow to accept connections during a parallel deploy would
        // turn into a failed release. Retry here instead.
        if (!await EnsureConsumerGroupAsync(db, stoppingToken)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var entries = await db.StreamReadGroupAsync(
                    StreamName, GroupName, _consumerName, position: ">", count: 10);

                if (entries.Length == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
                    continue;
                }

                foreach (var entry in entries)
                {
                    try
                    {
                        await ProcessEntryAsync(entry, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to apply summary result {EntryId}", entry.Id);
                    }
                    finally
                    {
                        // Acknowledged either way. A summary that could not be applied is not
                        // worth redelivering forever — the requester can simply ask again,
                        // and a stuck entry would block every later rewrite behind it.
                        await db.StreamAcknowledgeAsync(StreamName, GroupName, entry.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Summary result consumer loop failed");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task ProcessEntryAsync(StreamEntry entry, CancellationToken ct)
    {
        var fields = entry.Values.ToDictionary(
            value => value.Name.ToString(),
            value => value.Value.ToString());

        if (!Guid.TryParse(fields.GetValueOrDefault("room_id"), out var roomId)) return;

        var status = fields.GetValueOrDefault("status", string.Empty);
        if (!string.Equals(status, "completed", StringComparison.Ordinal))
        {
            // A failure carries its reason, and it is the requester's answer — logged rather
            // than written over a summary that is still perfectly good.
            _logger.LogWarning(
                "Summary rewrite for room {RoomId} failed: {Error}",
                roomId,
                fields.GetValueOrDefault("error", "no reason given"));
            return;
        }

        var content = fields.GetValueOrDefault("content_json", string.Empty);
        if (string.IsNullOrWhiteSpace(content)) return;

        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var artifacts = await unitOfWork.TranslationRoomArtifactRepository
            .GetArtifactsByRoomIdAsync(roomId, ct);

        var summary = artifacts?
            .Where(artifact => artifact.ArtifactType == ArtifactType.SUMMARY_EXPORT.ToString())
            .OrderByDescending(artifact => artifact.CreatedAt)
            .FirstOrDefault();

        if (summary == null)
        {
            // Nothing to replace. Rewriting is defined as replacing the meeting's summary, so
            // inventing one here would create an artifact the finalizer never made and whose
            // other columns nobody set.
            _logger.LogWarning("No summary artifact to rewrite for room {RoomId}", roomId);
            return;
        }

        summary.Content = content;
        // Moved here and nowhere else on this path. Without it "is this summary out of date?"
        // could only ever answer yes, because regenerating would not clear the comparison — and a
        // staleness warning that cannot turn itself off stops meaning anything.
        summary.UpdatedAt = DateTime.UtcNow;
        unitOfWork.TranslationRoomArtifactRepository.Update(summary);
        await unitOfWork.SaveChangesAsync(ct);

        // The indexed copy has to follow the artifact. A rewrite that only updated the
        // artifact would leave the Knowledge page and WarpBot answering from the summary this
        // room no longer has — the chunk ids are derived from the room, so this overwrites the
        // previous points rather than adding a second version alongside them.
        await ReindexSummaryAsync(scope, roomId, content, ct);

        _logger.LogInformation(
            "Rewrote the summary for room {RoomId} using template {TemplateKey}",
            roomId,
            fields.GetValueOrDefault("template_key", "general"));
    }

    /// <summary>
    /// Re-publishes the rewritten summary to the workspace knowledge index.
    ///
    /// Isolated in its own try/catch: the rewrite the user asked for has already been saved,
    /// and failing to re-index it must not turn a successful rewrite into a logged failure.
    /// </summary>
    private async Task ReindexSummaryAsync(
        IServiceScope scope, Guid roomId, string content, CancellationToken ct)
    {
        try
        {
            var text = MeetingSummaryKnowledgeText.Build(content);
            if (string.IsNullOrWhiteSpace(text)) return;

            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var room = await unitOfWork.TranslationRoomRepository.GetByIdAsync(roomId, ct);
            if (room == null || room.WorkspaceId == Guid.Empty) return;

            var publisher = scope.ServiceProvider.GetRequiredService<IKnowledgeFactRequestPublisher>();
            await publisher.PublishAsync(
                room.WorkspaceId,
                "meeting_summary",
                roomId,
                room.Title,
                text,
                indexSourceText: true,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not re-index the rewritten summary for room {RoomId}", roomId);
        }
    }

    private async Task<bool> EnsureConsumerGroupAsync(IDatabase db, CancellationToken ct)
    {
        var retryDelay = TimeSpan.FromSeconds(2);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await db.StreamCreateConsumerGroupAsync(StreamName, GroupName, "0", createStream: true);
                return true;
            }
            catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
            {
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(
                    ex,
                    "Could not create the '{Group}' consumer group on '{Stream}'; retrying in {Delay}.",
                    GroupName,
                    StreamName,
                    retryDelay);
                await Task.Delay(retryDelay, ct);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }

        return false;
    }
}

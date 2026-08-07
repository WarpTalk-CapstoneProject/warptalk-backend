using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Workers;

/// <summary>
/// Consumes domain events from the Workspace Service via Redis Streams to enforce cross-service business rules 
/// like cascading deletions and realtime member evictions.
/// </summary>
public class WorkspaceEventConsumerWorker : BackgroundService
{
    private readonly IRedisStreamRepository _redisStreamRepository;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<WorkspaceEventConsumerWorker> _logger;

    private const string StreamName = "workspace-events";
    private const string GroupName = "translation-room-group";
    private const string ConsumerName = "translation-room-consumer";

    public WorkspaceEventConsumerWorker(
        IRedisStreamRepository redisStreamRepository,
        IServiceProvider serviceProvider,
        IConnectionMultiplexer redis,
        ILogger<WorkspaceEventConsumerWorker> logger)
    {
        _redisStreamRepository = redisStreamRepository;
        _serviceProvider = serviceProvider;
        _redis = redis;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await EnsureConsumerGroupAsync(stoppingToken))
            return;

        _logger.LogInformation("WorkspaceEventConsumerWorker started consuming from stream '{StreamName}'.", StreamName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var messages = await _redisStreamRepository.ReadGroupAsync(StreamName, GroupName, ConsumerName, count: 10);

                foreach (var message in messages)
                {
                    if (message.Values.TryGetValue("event_type", out var eventType))
                    {
                        if (eventType == "WorkspaceDeleted")
                        {
                            await HandleWorkspaceDeleted(message, stoppingToken);
                        }
                        else if (eventType == "MemberRemoved")
                        {
                            await HandleMemberRemoved(message, stoppingToken);
                        }
                    }

                    await _redisStreamRepository.AcknowledgeAsync(StreamName, GroupName, message.Id);
                }

                if (messages.Count == 0)
                {
                    await Task.Delay(1000, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error consuming from workspace-events stream");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    /// <summary>
    /// GUARDED: this was a bare call outside every try, and IRedisStreamRepository only swallows
    /// BUSYGROUP — so an unreachable Redis threw XGROUP out of <see cref="ExecuteAsync"/> and
    /// tripped BackgroundServiceExceptionBehavior.StopHost, killing TranslationRoomService rather
    /// than just this worker. Retries with bounded backoff so consumption resumes on its own once
    /// Redis returns.
    /// </summary>
    /// <returns>true once the group exists; false only when the host is shutting down.</returns>
    private async Task<bool> EnsureConsumerGroupAsync(CancellationToken ct)
    {
        var retryDelay = TimeSpan.FromSeconds(2);
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _redisStreamRepository.EnsureConsumerGroupExistsAsync(StreamName, GroupName);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                attempt++;
                _logger.LogError(
                    ex,
                    "WorkspaceEventConsumerWorker could not create consumer group {Group} on {Stream} "
                    + "(attempt {Attempt}); retrying in {RetryDelay}. Workspace deletions and member "
                    + "removals are NOT reaching translation rooms until it succeeds.",
                    GroupName, StreamName, attempt, retryDelay);

                try
                {
                    await Task.Delay(retryDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }

        return false;
    }

    private async Task HandleWorkspaceDeleted(RedisStreamMessage message, CancellationToken ct)
    {
        if (!message.Values.TryGetValue("workspace_id", out var workspaceIdStr) || !Guid.TryParse(workspaceIdStr, out var workspaceId)) return;

        _logger.LogInformation("Received WorkspaceDeletedEvent for Workspace: {WorkspaceId}", workspaceId);

        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var inProgressRooms = await unitOfWork.TranslationRoomRepository.FindAsync(
            r => r.WorkspaceId == workspaceId && r.Status == RoomStatus.IN_PROGRESS.ToString(), ct: ct);

        if (inProgressRooms == null || inProgressRooms.Count == 0) return;

        var db = _redis.GetDatabase();

        foreach (var room in inProgressRooms)
        {
            room.Status = RoomStatus.CANCELLED.ToString();
            room.EndedAt = DateTime.UtcNow;
            unitOfWork.TranslationRoomRepository.Update(room);

            var payload = JsonSerializer.Serialize(new { Command = "CancelRoom", RoomId = room.Id.ToString() });
            await db.PublishAsync(RedisChannel.Literal("warptalk:translation-room:commands"), payload);
        }

        await unitOfWork.SaveChangesAsync(ct);
    }

    private async Task HandleMemberRemoved(RedisStreamMessage message, CancellationToken ct)
    {
        if (!message.Values.TryGetValue("user_id", out var userIdStr) || !Guid.TryParse(userIdStr, out var userId)) return;

        _logger.LogInformation("Received MemberRemovedEvent for User: {UserId}", userId);

        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var activeParticipants = await unitOfWork.TranslationRoomParticipantRepository.FindAsync(
            p => p.UserId == userId && p.Status == TranslationRoomParticipantStatuses.Connected, ct: ct);

        if (activeParticipants == null || activeParticipants.Count == 0) return;

        var db = _redis.GetDatabase();

        foreach (var p in activeParticipants)
        {
            p.Status = TranslationRoomParticipantStatuses.Kicked;
            p.LeftAt = DateTime.UtcNow;
            unitOfWork.TranslationRoomParticipantRepository.Update(p);

            var payload = JsonSerializer.Serialize(new { Command = "Kick", RoomId = p.TranslationRoomId.ToString(), UserId = p.UserId.ToString() });
            await db.PublishAsync(RedisChannel.Literal("warptalk:translation-room:commands"), payload);
        }

        await unitOfWork.SaveChangesAsync(ct);
    }
}

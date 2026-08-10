using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Configuration;
using NotificationClient = WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient;
using NotificationRequest = WarpTalk.Shared.Protos.SendNotificationRequest;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Workers;

public class ArtifactsFinalizationWorker : BackgroundService
{
    private readonly IArtifactsFinalizationQueue _queue;
    private readonly IServiceProvider _serviceProvider;
    private readonly NotificationClient _notificationClient;
    private readonly ILogger<ArtifactsFinalizationWorker> _logger;
    private readonly string _frontendBaseUrl;

    private const string SummaryReadyNotificationType = "MEETING_SUMMARY_READY";

    public ArtifactsFinalizationWorker(
        IArtifactsFinalizationQueue queue,
        IServiceProvider serviceProvider,
        NotificationClient notificationClient,
        IOptions<AppSettings> appSettings,
        ILogger<ArtifactsFinalizationWorker> logger)
    {
        _queue = queue;
        _serviceProvider = serviceProvider;
        _notificationClient = notificationClient;
        _frontendBaseUrl = appSettings.Value.FrontendBaseUrl?.TrimEnd('/') ?? string.Empty;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ArtifactsFinalizationWorker starting...");
        using var concurrency = new SemaphoreSlim(4, 4);
        var running = new List<Task>();

        try
        {
            await foreach (var roomId in _queue.ReadAllAsync(stoppingToken))
            {
                await concurrency.WaitAsync(stoppingToken);
                running.RemoveAll(static task => task.IsCompleted);
                running.Add(ProcessRoomAsync(roomId, stoppingToken, concurrency));
            }

            await Task.WhenAll(running);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical error reading from finalization channel");
        }

        _logger.LogInformation("ArtifactsFinalizationWorker stopping.");
    }

    private async Task ProcessRoomAsync(
        Guid roomId,
        CancellationToken stoppingToken,
        SemaphoreSlim concurrency)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var finalizationService = scope.ServiceProvider.GetRequiredService<IArtifactsFinalizer>();
            await finalizationService.ProcessRoomFinalizationAsync(roomId, stoppingToken);

            // The summary exists as of this line, and this is the only moment anything knows
            // that. Finalization is the last step of a meeting nobody is watching any more —
            // everyone has left, which is exactly why they need telling.
            await NotifySummaryReadyAsync(scope, roomId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing finalization for room {RoomId}", roomId);
        }
        finally
        {
            concurrency.Release();
        }
    }

    /// <summary>
    /// Tells everyone who was in the meeting that its summary is ready.
    ///
    /// Failures are logged and swallowed on purpose: the artifacts are already written and
    /// durable by the time this runs, and letting a notification outage roll back — or even
    /// appear to roll back — a finalization would trade the valuable thing for the cheap one.
    /// </summary>
    private async Task NotifySummaryReadyAsync(
        IServiceScope scope,
        Guid roomId,
        CancellationToken ct)
    {
        try
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var rooms = await unitOfWork.TranslationRoomRepository.FindAsync(
                room => room.Id == roomId,
                "TranslationRoomParticipants",
                ct);
            var room = rooms.FirstOrDefault();
            if (room == null)
            {
                return;
            }

            // The room's own page, not the meeting: the meeting is over, and the summary is
            // read where the transcript and the artifacts already live.
            var link = $"{_frontendBaseUrl}/rooms/{room.Id}";

            foreach (var userId in ResolveRecipientIds(room))
            {
                var request = new NotificationRequest
                {
                    UserId = userId.ToString(),
                    Type = SummaryReadyNotificationType,
                    Title = $"Summary ready for \"{room.Title}\"",
                    Body = $"The summary and transcript for \"{room.Title}\" are ready to read.",
                    ActionUrl = link,
                };
                request.Metadata.Add("room_id", room.Id.ToString());
                request.Metadata.Add("room_title", room.Title);

                try
                {
                    await _notificationClient.SendNotificationAsync(request, cancellationToken: ct);
                }
                catch (Exception ex)
                {
                    // Per recipient, so one unreachable user does not cost the rest theirs.
                    _logger.LogError(
                        ex,
                        "Failed to send summary-ready notification for room {RoomId} to user {UserId}",
                        roomId,
                        userId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to announce the summary for room {RoomId}", roomId);
        }
    }

    /// <summary>The host plus every participant who was signed in. Same rule as the reminder.</summary>
    private static List<Guid> ResolveRecipientIds(TranslationRoom room)
    {
        var ids = new HashSet<Guid> { room.HostId };
        foreach (var participant in room.TranslationRoomParticipants)
        {
            if (participant.UserId.HasValue)
            {
                ids.Add(participant.UserId.Value);
            }
        }
        return ids.ToList();
    }
}

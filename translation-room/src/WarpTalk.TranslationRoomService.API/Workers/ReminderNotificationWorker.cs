using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Configuration;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using NotificationClient = WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient;
using NotificationRequest = WarpTalk.Shared.Protos.SendNotificationRequest;

namespace WarpTalk.TranslationRoomService.API.Workers;

/// <summary>
/// WT-14: mirrors IdleRoomMonitoringWorker's polling shape. Every minute, checks SCHEDULED
/// rooms against the T-10min / T-1min reminder windows (ReminderWindowEvaluator) and — for any
/// room that enters a window and hasn't been reminded for it yet — pushes a notification via
/// the SAME gRPC path other services use to create user notifications
/// (NotificationGrpcServiceImpl.SendNotification, which persists + Redis-publishes so
/// NotificationHub relays it to "user:{userId}" in real time — see NotificationRedisSubscriberService).
/// The reminder_10min_sent_at/reminder_1min_sent_at columns are stamped right after sending so a
/// restart or a slow poll never double-sends for the same window.
/// </summary>
public class ReminderNotificationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly NotificationClient? _notificationClient;
    private readonly ILogger<ReminderNotificationWorker> _logger;
    private readonly string _frontendBaseUrl;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);

    private const string NotificationType = "MEETING_REMINDER";

    public ReminderNotificationWorker(
        IServiceProvider serviceProvider,
        ILogger<ReminderNotificationWorker> logger,
        IOptions<AppSettings>? appSettings = null,
        NotificationClient? notificationClient = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _frontendBaseUrl = appSettings?.Value.FrontendBaseUrl ?? "http://localhost:3000";
        _notificationClient = notificationClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ReminderNotificationWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndSendRemindersAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ReminderNotificationWorker");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }
    }

    private async Task CheckAndSendRemindersAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var roomRepo = unitOfWork.TranslationRoomRepository;

        var now = DateTime.UtcNow;

        // Candidates: still SCHEDULED, has a ScheduledAt, and at least one window unsent.
        // The precise window check (which is much cheaper to keep correct as pure logic) happens
        // in-memory via ReminderWindowEvaluator, same as IdleRoomMonitoringWorker's idle check.
        var candidates = await roomRepo.FindAsync(
            r => r.Status == "SCHEDULED"
                 && r.ScheduledAt != null
                 && (r.Reminder10MinSentAt == null || r.Reminder1MinSentAt == null),
            "TranslationRoomParticipants",
            ct);

        foreach (var room in candidates)
        {
            if (ReminderWindowEvaluator.ShouldSendReminder(room.ScheduledAt!.Value, now, room.Reminder10MinSentAt, ReminderWindowEvaluator.TenMinuteWindow))
            {
                await SendReminderAsync(room, minutesUntilStart: 10, ct);
                room.Reminder10MinSentAt = now;
                roomRepo.Update(room);
            }

            if (ReminderWindowEvaluator.ShouldSendReminder(room.ScheduledAt!.Value, now, room.Reminder1MinSentAt, ReminderWindowEvaluator.OneMinuteWindow))
            {
                await SendReminderAsync(room, minutesUntilStart: 1, ct);
                room.Reminder1MinSentAt = now;
                roomRepo.Update(room);
            }
        }

        await unitOfWork.SaveChangesAsync(ct);
    }

    private async Task SendReminderAsync(TranslationRoom room, int minutesUntilStart, CancellationToken ct)
    {
        if (_notificationClient == null)
        {
            _logger.LogWarning("ReminderNotificationWorker: NotificationGrpcServiceClient is not configured; skipping reminder for room {RoomId}.", room.Id);
            return;
        }

        var recipientIds = ResolveRecipientIds(room);
        var joinLink = $"{_frontendBaseUrl}/room/{room.TranslationRoomCode}";
        var title = minutesUntilStart == 1
            ? $"\"{room.Title}\" starts in 1 minute"
            : $"\"{room.Title}\" starts in {minutesUntilStart} minutes";

        foreach (var userId in recipientIds)
        {
            try
            {
                var request = new NotificationRequest
                {
                    UserId = userId.ToString(),
                    Type = NotificationType,
                    Title = title,
                    Body = $"Your meeting \"{room.Title}\" is starting soon. Join at {joinLink}.",
                    ActionUrl = joinLink,
                };
                request.Metadata.Add("room_id", room.Id.ToString());
                request.Metadata.Add("room_title", room.Title);
                request.Metadata.Add("minutes_until_start", minutesUntilStart.ToString());

                await _notificationClient.SendNotificationAsync(request, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ReminderNotificationWorker: failed to send T-{Minutes}min reminder for room {RoomId} to user {UserId}", minutesUntilStart, room.Id, userId);
            }
        }
    }

    /// <summary>
    /// Known userIds for a SCHEDULED room: the host (always a participant, auto-added at
    /// creation — see TranslationRoomService.CreateTranslationRoomAsync) plus anyone else
    /// already in TranslationRoomParticipants. Invited-by-email recipients who haven't joined
    /// yet have no linked userId at this point (TranslationRoomInvitation only stores an email),
    /// so they only ever received the invitation email sent at creation time — not this in-app
    /// reminder. That is a known, accepted scope limit.
    /// </summary>
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

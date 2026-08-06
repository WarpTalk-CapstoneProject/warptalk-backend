using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
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
/// WT-14: mirrors IdleRoomMonitoringWorker's polling shape. Every minute, checks rooms that have
/// not started yet against the T-10min / T-1min reminder windows (ReminderWindowEvaluator) and —
/// for any room that enters a window and hasn't been reminded for it yet — pushes a notification
/// via the SAME gRPC path other services use to create user notifications
/// (NotificationGrpcServiceImpl.SendNotification, which persists + Redis-publishes so
/// NotificationHub relays it to "user:{userId}" in real time — see NotificationRedisSubscriberService).
/// The reminder_10min_sent_at/reminder_1min_sent_at columns are stamped right after sending so a
/// restart or a slow poll never double-sends for the same window.
///
/// WT-326 changed two things about that:
///   * the candidate sweep no longer looks only at SCHEDULED rooms (see CheckAndSendRemindersAsync);
///   * "already reminded" is now tracked per RECIPIENT as well as per room+window
///     (see SendReminderAsync), so one failing recipient no longer re-notifies the whole room.
/// </summary>
public class ReminderNotificationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly NotificationClient _notificationClient;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<ReminderNotificationWorker> _logger;
    private readonly string _frontendBaseUrl;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);

    private const string NotificationType = "MEETING_REMINDER";

    /// <summary>
    /// WT-326. How long a per-recipient "already reminded" marker lives. Only has to outlive the
    /// widest reminder window plus the retries inside it; the reminder_Nmin_sent_at columns remain
    /// the durable record, so an expired marker costs at most one duplicate notification.
    /// </summary>
    private static readonly TimeSpan RecipientSentMarkerTtl = TimeSpan.FromHours(1);

    public ReminderNotificationWorker(
        IServiceProvider serviceProvider,
        ILogger<ReminderNotificationWorker> logger,
        IOptions<AppSettings> appSettings,
        NotificationClient notificationClient,
        IConnectionMultiplexer redis)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _frontendBaseUrl = appSettings.Value.FrontendBaseUrl;
        _notificationClient = notificationClient;
        _redis = redis;
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

    /// <summary>One poll. Internal so the tests can drive it directly — see InternalsVisibleTo.</summary>
    internal async Task CheckAndSendRemindersAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var roomRepo = unitOfWork.TranslationRoomRepository;

        var now = DateTime.UtcNow;

        // Candidates: not started yet, inside the widest reminder lead time, at least one window
        // unsent. The predicate lives with the windows themselves (ReminderWindowEvaluator
        // .SweepCandidateFilter) because the two must agree — see the WT-326 note there for why
        // it can no longer be `Status == "SCHEDULED"`. The precise per-window check (much cheaper
        // to keep correct as pure logic) still happens in-memory below, same as
        // IdleRoomMonitoringWorker's idle check.
        var candidates = await roomRepo.FindAsync(
            ReminderWindowEvaluator.SweepCandidateFilter(now),
            "TranslationRoomParticipants",
            ct);

        foreach (var room in candidates)
        {
            if (ReminderWindowEvaluator.ShouldSendReminder(room.ScheduledAt!.Value, now, room.Reminder10MinSentAt, ReminderWindowEvaluator.TenMinuteWindow))
            {
                await TrySendReminderOnceAsync(room, 10, now, roomRepo, unitOfWork, ct);
            }

            if (ReminderWindowEvaluator.ShouldSendReminder(room.ScheduledAt!.Value, now, room.Reminder1MinSentAt, ReminderWindowEvaluator.OneMinuteWindow))
            {
                await TrySendReminderOnceAsync(room, 1, now, roomRepo, unitOfWork, ct);
            }
        }

        await unitOfWork.SaveChangesAsync(ct);
    }

    private async Task TrySendReminderOnceAsync(
        TranslationRoom room,
        int minutesUntilStart,
        DateTime sentAt,
        ITranslationRoomRepository roomRepo,
        IUnitOfWork unitOfWork,
        CancellationToken ct)
    {
        var database = _redis.GetDatabase();
        var lockKey = $"warptalk:reminder-lock:{room.Id}:{minutesUntilStart}";
        var lockToken = Guid.NewGuid().ToString("N");
        if (!await database.LockTakeAsync(lockKey, lockToken, TimeSpan.FromMinutes(2)))
        {
            return;
        }

        try
        {
            if (!await SendReminderAsync(room, minutesUntilStart, ct))
            {
                return;
            }

            if (minutesUntilStart == 10)
            {
                room.Reminder10MinSentAt = sentAt;
            }
            else
            {
                room.Reminder1MinSentAt = sentAt;
            }
            roomRepo.Update(room);
            await unitOfWork.SaveChangesAsync(ct);
        }
        finally
        {
            await database.LockReleaseAsync(lockKey, lockToken);
        }
    }

    /// <summary>
    /// WT-326. Sends the reminder to every recipient who has not already received it for this
    /// (room, window), and returns whether the room as a whole is now fully notified.
    ///
    /// The bug this fixes: the return value used to be the ONLY idempotency signal, and
    /// TrySendReminderOnceAsync stamps reminder_Nmin_sent_at only when it is true. One transient
    /// gRPC failure to one person in a five-person room therefore left the column null, and the
    /// next poll — a minute later, and every minute after that for the rest of the window —
    /// re-sent to all five. The Redis lock is per (room, window), so it does not stop this: the
    /// resend is the next poll, not a concurrent one.
    ///
    /// The fix records a per-recipient marker in Redis and skips anyone who already has one, so a
    /// retry costs one send per FAILED recipient instead of one per recipient. The durable column
    /// still means "everybody got it" and is still only stamped when that is true — see
    /// TrySendReminderOnceAsync — which is what keeps the retry alive for whoever was missed.
    ///
    /// The marker is written AFTER a successful send, never before: a crash in between then
    /// re-sends (at-least-once), which is the same direction the column stamping already errs in
    /// and the right one for a reminder. The TTL only has to outlive the window — the column, not
    /// Redis, is the durable record — so an evicted or expired marker degrades to exactly the old
    /// behaviour rather than to a lost reminder.
    /// </summary>
    private async Task<bool> SendReminderAsync(
        TranslationRoom room,
        int minutesUntilStart,
        CancellationToken ct)
    {
        var database = _redis.GetDatabase();
        var recipientIds = ResolveRecipientIds(room);
        var joinLink = $"{_frontendBaseUrl}/room/{room.TranslationRoomCode}";
        var title = minutesUntilStart == 1
            ? $"\"{room.Title}\" starts in 1 minute"
            : $"\"{room.Title}\" starts in {minutesUntilStart} minutes";

        var allSent = true;
        foreach (var userId in recipientIds)
        {
            var recipientKey = RecipientSentKey(room.Id, minutesUntilStart, userId);
            if (await database.KeyExistsAsync(recipientKey))
            {
                _logger.LogDebug(
                    "ReminderNotificationWorker: user {UserId} already has the T-{Minutes}min reminder for room {RoomId}; not re-sending.",
                    userId, minutesUntilStart, room.Id);
                continue;
            }

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
                await database.StringSetAsync(recipientKey, DateTime.UtcNow.ToString("O"), RecipientSentMarkerTtl);
            }
            catch (Exception ex)
            {
                allSent = false;
                _logger.LogError(ex, "ReminderNotificationWorker: failed to send T-{Minutes}min reminder for room {RoomId} to user {UserId}", minutesUntilStart, room.Id, userId);
            }
        }

        return allSent;
    }

    internal static string RecipientSentKey(Guid roomId, int minutesUntilStart, Guid userId)
        => $"warptalk:reminder-sent:{roomId}:{minutesUntilStart}:{userId}";

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

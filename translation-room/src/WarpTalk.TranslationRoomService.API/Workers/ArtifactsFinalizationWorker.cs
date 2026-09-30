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
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.Shared.Coordination;

namespace WarpTalk.TranslationRoomService.API.Workers;

public class ArtifactsFinalizationWorker : BackgroundService
{
    private readonly IArtifactsFinalizationQueue _queue;
    private readonly IServiceProvider _serviceProvider;
    private readonly IDistributedLockProvider _locks;
    private readonly NotificationClient _notificationClient;
    private readonly ILogger<ArtifactsFinalizationWorker> _logger;
    private readonly string _frontendBaseUrl;

    private const string SummaryReadyNotificationType = "MEETING_SUMMARY_READY";

    /// <summary>
    /// Only bounds how long a replica that died mid-finalization keeps the room: the lease is
    /// renewed for as long as the finalization runs (the flush alone waits up to 30 s).
    /// </summary>
    private static readonly TimeSpan FinalizationLease = TimeSpan.FromMinutes(2);

    /// <summary>One lease per room, so different meetings still finalize in parallel.</summary>
    internal static string LockResourceFor(Guid roomId) => $"translation-room:finalize:{roomId}";

    public ArtifactsFinalizationWorker(
        IArtifactsFinalizationQueue queue,
        IServiceProvider serviceProvider,
        NotificationClient notificationClient,
        IOptions<AppSettings> appSettings,
        ILogger<ArtifactsFinalizationWorker> logger,
        IDistributedLockProvider locks)
    {
        _queue = queue;
        _serviceProvider = serviceProvider;
        _locks = locks;
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
            await foreach (var request in _queue.ReadAllAsync(stoppingToken))
            {
                await concurrency.WaitAsync(stoppingToken);
                running.RemoveAll(static task => task.IsCompleted);
                running.Add(ProcessRoomAsync(request, stoppingToken, concurrency));
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
        FinalizationRequest request,
        CancellationToken stoppingToken,
        SemaphoreSlim concurrency)
    {
        var roomId = request.RoomId;

        try
        {
            await FinalizeOnceAsync(request, stoppingToken);
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
    /// Finalizes the room unless it is being, or has already been, finalized — on any replica.
    ///
    /// The queue is in-process, but its producers are not: an End served by one replica, the
    /// reconciliation sweep on another, a host's "regenerate" on a third can each queue the same
    /// room, and every finalization writes a NEW transcript and summary artifact and rings
    /// "Summary ready" for everyone who was there. Two guards, one per way that goes wrong:
    ///   * a per-room lease, so two replicas never finalize the same room at the same time;
    ///   * inside it, the finalizer's own output as the record — it writes both text artifacts in
    ///     one save or neither, and every legitimate producer only queues a room that has none —
    ///     so a request that arrives after a finished finalization is recognised and dropped.
    /// Internal so the tests can drive two "replicas" against one lease store.
    /// </summary>
    internal async Task<ExclusiveTickOutcome> FinalizeOnceAsync(FinalizationRequest request, CancellationToken stoppingToken)
    {
        var roomId = request.RoomId;
        var outcome = await _locks.TryRunExclusiveAsync(
            LockResourceFor(roomId),
            FinalizationLease,
            async ct =>
            {
                using var scope = _serviceProvider.CreateScope();
                if (await IsAlreadyFinalizedAsync(scope, roomId, ct))
                {
                    _logger.LogInformation(
                        "Room {RoomId} already has its transcript and summary artifacts; not finalizing it again.",
                        roomId);
                    return;
                }

                var finalizationService = scope.ServiceProvider.GetRequiredService<IArtifactsFinalizer>();
                await finalizationService.ProcessRoomFinalizationAsync(
                    request.RoomId, request.TemplateKey, request.SummaryLanguage, ct);

                // The summary exists as of this line, and this is the only moment anything knows
                // that. Finalization is the last step of a meeting nobody is watching any more —
                // everyone has left, which is exactly why they need telling.
                await NotifySummaryReadyAsync(scope, roomId, ct);
            },
            _logger,
            stoppingToken);

        if (outcome == ExclusiveTickOutcome.Skipped)
        {
            _logger.LogInformation(
                "Room {RoomId} is being finalized by another replica; dropping this duplicate request.",
                roomId);
        }

        return outcome;
    }

    private static async Task<bool> IsAlreadyFinalizedAsync(IServiceScope scope, Guid roomId, CancellationToken ct)
    {
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var transcript = ArtifactType.TRANSCRIPT_EXPORT.ToString();
        var summary = ArtifactType.SUMMARY_EXPORT.ToString();
        return await unitOfWork.TranslationRoomArtifactRepository.AnyAsync(
            artifact => artifact.TranslationRoomId == roomId
                && artifact.DeletedAt == null
                && (artifact.ArtifactType == transcript || artifact.ArtifactType == summary),
            ct);
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

            // WT-870: a meeting that kept no transcript has no summary to announce, and a
            // "summary and transcript are ready" message would send people to look for both.
            if (!TranscriptRetention.IsSaved(room))
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

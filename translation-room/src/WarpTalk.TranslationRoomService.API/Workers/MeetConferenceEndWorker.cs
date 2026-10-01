using System.Collections.Concurrent;
using WarpTalk.Shared;
using WarpTalk.Shared.Coordination;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Workers;

/// <summary>
/// Ends a Google Meet bridge room when the Google Meet conference it bridges ends. The bridge
/// popup has no End button, so this is how a bridge meeting is meant to finish.
///
/// Every 30 s, for each open EXTERNAL_BRIDGE room with a Meet link, asks Google — through
/// AssistantService, with the host's own Google grant — for the conference records of the room's
/// Meet code, and lets <see cref="MeetConferenceEndPolicy"/> decide. A room is ended through
/// <c>EndTranslationRoomAsync(room.Id, room.HostId)</c>, the same path IdleRoomMonitoringWorker uses,
/// under the same <see cref="RoomEndingSweepLock"/>.
///
/// A host who has not connected the google_meet plugin in the room's workspace
/// (<c>plugin_not_connected</c>), has not granted meetings.space.readonly, or has no Google
/// connection is not an error: the room is skipped — logged once per room per reason — and the
/// existing reapers (idle, abandoned) stay in charge of it. It is asked again every tick, so a host
/// who connects the plugin mid-meeting is picked up. Any failure to read Google is "unknown",
/// never "ended".
/// </summary>
public class MeetConferenceEndWorker : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly string[] OpenStatuses = ["WAITING", "IN_PROGRESS"];

    private readonly IServiceProvider _serviceProvider;
    private readonly IDistributedLockProvider _locks;
    private readonly ILogger<MeetConferenceEndWorker> _logger;

    // roomId -> the last reason it was skipped for. Logged only when it changes, so a host without
    // the scope does not write a line every 30 s for the length of the meeting.
    private readonly ConcurrentDictionary<Guid, string> _lastSkip = new();

    public MeetConferenceEndWorker(
        IServiceProvider serviceProvider,
        IDistributedLockProvider locks,
        ILogger<MeetConferenceEndWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _locks = locks;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _locks.TryRunExclusiveAsync(
                    RoomEndingSweepLock.Resource,
                    LeaseDuration,
                    CheckAsync,
                    _logger,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in MeetConferenceEndWorker");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One tick. Internal so the tests can drive it directly — see InternalsVisibleTo.</summary>
    internal async Task CheckAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var roomRepo = scope.ServiceProvider.GetRequiredService<ITranslationRoomRepository>();
        var roomService = scope.ServiceProvider.GetRequiredService<ITranslationRoomService>();
        var meet = scope.ServiceProvider.GetRequiredService<IMeetConferenceRecordsClient>();

        var rooms = await roomRepo.FindAsync(
            r => OpenStatuses.Contains(r.Status)
                && r.TranslationRoomType == ExternalBridgeConstants.RoomType
                && r.ExternalMeetingUrl != null
                && r.ExternalMeetingUrl != "",
            "",
            ct);

        var seen = new HashSet<Guid>();
        foreach (var room in rooms)
        {
            ct.ThrowIfCancellationRequested();
            seen.Add(room.Id);

            var lookup = await meet.GetRecordsAsync(room.EffectiveHostId, room.WorkspaceId, room.ExternalMeetingUrl!, ct);
            if (!lookup.IsSuccess)
            {
                if (lookup.ErrorCode is MeetConferenceErrorCodes.ProviderUnavailable or MeetConferenceErrorCodes.ProviderRateLimited)
                {
                    _logger.LogDebug("Meet conference lookup for room {RoomId} failed transiently: {Error}", room.Id, lookup.ErrorCode);
                }
                else if (_lastSkip.TryGetValue(room.Id, out var previous) is false || previous != lookup.ErrorCode)
                {
                    _lastSkip[room.Id] = lookup.ErrorCode!;
                    _logger.LogInformation(
                        "Room {RoomId}: cannot watch its Google Meet ({Reason}); leaving it to the other reapers.",
                        room.Id,
                        lookup.ErrorCode);
                }

                continue;
            }

            var verdict = MeetConferenceEndPolicy.Decide(
                lookup.Records,
                DateTime.SpecifyKind(room.CreatedAt, DateTimeKind.Utc),
                room.StartedAt is { } started ? DateTime.SpecifyKind(started, DateTimeKind.Utc) : null);
            if (verdict != MeetConferenceEndVerdict.End)
                continue;

            _logger.LogInformation("Room {RoomId}: its Google Meet conference has ended. Ending the room.", room.Id);
            var result = await roomService.EndTranslationRoomAsync(room.Id, room.HostId, ct);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Failed to end room {RoomId} after its Meet ended: {Error}", room.Id, result.Error);
                continue;
            }

            _lastSkip.TryRemove(room.Id, out _);
        }

        // Forget rooms that are no longer open, so the map does not grow for the process lifetime.
        foreach (var roomId in _lastSkip.Keys)
        {
            if (!seen.Contains(roomId)) _lastSkip.TryRemove(roomId, out _);
        }
    }
}

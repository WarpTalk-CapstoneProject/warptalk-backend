using System.Threading.Channels;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;

namespace WarpTalk.MeetingService.API.Workers;

/// <summary>
/// Stops a Google Meet bridge recording when the bridge session ends, instead of letting it run on
/// into LiveKit's empty-room timeout. The decision is <see cref="IMeetingRoomService.StopRecordingIfBridgeEndedAsync"/>
/// (rules in <see cref="BridgeRecordingEndPolicy"/>); this only carries each departure there and
/// asks again when the answer is "empty, but inside the grace period".
///
/// IN-PROCESS ON PURPOSE. LiveKit delivers each webhook to one replica, so the replica that heard
/// the departure is the one that follows it up; the stop itself is made once per egress across
/// replicas by a Redis marker. A restart mid-grace drops the follow-up, which costs exactly the
/// behaviour before this existed: LiveKit closes the empty room and the egress ends with it.
/// </summary>
public sealed class BridgeRecordingEndWorker : BackgroundService, IBridgeRecordingEndWatcher
{
    /// <summary>
    /// Bounds the follow-ups of one departure. Each AwaitingGrace answer names the time left; more
    /// than a few means departures keep landing, and each of those started its own follow-up.
    /// </summary>
    public const int MaxChecksPerDeparture = 4;

    private readonly Channel<BridgeRecordingEndRequest> _departures = Channel.CreateBounded<BridgeRecordingEndRequest>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BridgeRecordingEndWorker> _logger;
    private readonly TimeProvider _time;

    public BridgeRecordingEndWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<BridgeRecordingEndWorker> logger,
        TimeProvider? time = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        Grace = BridgeRecordingEndPolicy.GraceFromSeconds(configuration["Meeting:BridgeRecording:EndGraceSeconds"]);
    }

    /// <summary>How long an emptied bridge room waits for a reload or rejoin before its recording stops.</summary>
    public TimeSpan Grace { get; }

    public void NotifyParticipantLeft(BridgeRecordingEndRequest request) => _departures.Writer.TryWrite(request);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new List<Task>();
        try
        {
            await foreach (var departure in _departures.Reader.ReadAllAsync(stoppingToken))
            {
                running.RemoveAll(task => task.IsCompleted);
                running.Add(FollowDepartureAsync(departure, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await Task.WhenAll(running);
    }

    /// <summary>One departure, followed to an answer. Public so the tests can drive it.</summary>
    public async Task<BridgeRecordingEndCheck?> FollowDepartureAsync(BridgeRecordingEndRequest departure, CancellationToken ct)
    {
        BridgeRecordingEndCheck? check = null;
        try
        {
            for (var attempt = 1; attempt <= MaxChecksPerDeparture; attempt++)
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var rooms = scope.ServiceProvider.GetRequiredService<IMeetingRoomService>();
                    check = await rooms.StopRecordingIfBridgeEndedAsync(departure, _time.GetUtcNow().UtcDateTime, Grace, ct);
                }

                if (check.Outcome != BridgeRecordingEndOutcome.AwaitingGrace)
                    break;

                // A little past the deadline, so the next look is not answered "0.01 s to go".
                var wait = (check.RetryAfter ?? Grace) + TimeSpan.FromMilliseconds(250);
                await Task.Delay(wait, _time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not follow the departure from {RoomName} for its bridge recording.", departure.ProviderRoomName);
        }

        return check;
    }
}

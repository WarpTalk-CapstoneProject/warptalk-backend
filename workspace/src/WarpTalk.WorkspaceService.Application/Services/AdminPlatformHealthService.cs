using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Application.Services;

/// <summary>
/// Composes the System Health screen out of instant PromQL queries.
///
/// The queries are constants rather than strings built at the call site so a test can assert
/// exactly what is asked for: every one of these names an exporter series, and a rename on the
/// exporter side would otherwise show up as a permanently empty, unexplained section.
/// </summary>
public class AdminPlatformHealthService : IAdminPlatformHealthService
{
    /// <summary>Prefix the exporter puts in the <c>key</c> label of a heartbeat count.</summary>
    private const string HeartbeatKeyPrefix = "warptalk:worker:heartbeat:";

    public const string TargetsQuery = "up";
    public const string WorkersQuery = "redis_keys_count";
    public const string LagQuery = "redis_stream_group_lag";
    public const string PendingQuery = "redis_stream_group_messages_pending";
    public const string ConsumersQuery = "redis_stream_group_consumers";
    public const string DeadLetterQuery = "redis_stream_length";

    /// <summary>
    /// p95 over the last hour. <c>rate()</c> rather than the raw counter because the underlying
    /// bucket hashes carry a TTL — when one expires the counter restarts at zero, and a quantile
    /// over the raw series would read that reset as a cliff instead of ignoring it.
    /// </summary>
    public const string StageLatencyQuery =
        "histogram_quantile(0.95, sum by (stage, le) (rate(warptalk_stage_latency_ms_bucket[1h])))";

    /// <summary>First admitted join per room, last 24h (translation-room, MeetingLifecycleMetrics).</summary>
    public const string MeetingsStartedQuery =
        "sum(increase(warptalk_meeting_started_total[24h]))";

    /// <summary>Rooms ended in the last 24h, by who ended them and whether they reached live.</summary>
    public const string MeetingsEndedQuery =
        "sum by (end_reason, reached_live) (increase(warptalk_meeting_ended_total[24h]))";

    /// <summary>
    /// max, not sum: only the replica that ran the last sweep reports, and a replica with a
    /// stale snapshot withholds it — see MeetingLifecycleMetrics.RecordRoomSnapshot.
    /// </summary>
    public const string LiveRoomsQuery = "max(warptalk_meeting_live_rooms)";
    public const string OccupiedRoomsQuery = "max(warptalk_meeting_occupied_rooms)";

    /// <summary>Per-stage attempt outcomes the AI workers record (metrics_exporter, last hour).</summary>
    public const string StageOutcomesQuery =
        "sum by (stage, outcome) (increase(warptalk_stage_messages_total[1h]))";

    /// <summary>The pipeline stages the screen reports, in pipeline order.</summary>
    private static readonly string[] PipelineStages = ["stt", "translation", "tts"];

    private readonly IPlatformMetricsSource _metrics;
    private readonly IPlatformAlertSource _alerts;
    private readonly IOutboxDeadLetterReader _outbox;
    private readonly PlatformHealthOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AdminPlatformHealthService> _logger;

    public AdminPlatformHealthService(
        IPlatformMetricsSource metrics,
        IPlatformAlertSource alerts,
        IOutboxDeadLetterReader outbox,
        PlatformHealthOptions options,
        TimeProvider timeProvider,
        ILogger<AdminPlatformHealthService> logger)
    {
        _metrics = metrics;
        _alerts = alerts;
        _outbox = outbox;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<AdminPlatformHealthResponse> ReadAsync(CancellationToken ct = default)
    {
        var warnings = new ConcurrentBag<string>();
        var observedAt = _timeProvider.GetUtcNow().UtcDateTime;

        var targetsTask = QueryAsync(TargetsQuery, "scrape targets", warnings, ct);
        var workersTask = QueryAsync(WorkersQuery, "worker heartbeats", warnings, ct);
        var lagTask = QueryAsync(LagQuery, "stream lag", warnings, ct);
        var pendingTask = QueryAsync(PendingQuery, "stream pending", warnings, ct);
        var consumersTask = QueryAsync(ConsumersQuery, "stream consumers", warnings, ct);
        var deadLetterTask = QueryAsync(DeadLetterQuery, "dead-letter depth", warnings, ct);
        var latencyTask = QueryAsync(StageLatencyQuery, "stage latency", warnings, ct);
        var startedTask = QueryAsync(MeetingsStartedQuery, "meetings started", warnings, ct);
        var endedTask = QueryAsync(MeetingsEndedQuery, "meeting outcomes", warnings, ct);
        var liveRoomsTask = QueryAsync(LiveRoomsQuery, "live rooms", warnings, ct);
        var occupiedRoomsTask = QueryAsync(OccupiedRoomsQuery, "occupied rooms", warnings, ct);
        var stageOutcomesTask = QueryAsync(StageOutcomesQuery, "pipeline stage outcomes", warnings, ct);
        // Neither of these is Prometheus, so neither can make monitoring "unavailable": each
        // degrades to its own warning.
        var alertsTask = AlertsAsync(warnings, ct);
        var outboxTask = OutboxAsync(warnings, ct);

        try
        {
            await Task.WhenAll(
                targetsTask,
                workersTask,
                lagTask,
                pendingTask,
                consumersTask,
                deadLetterTask,
                latencyTask,
                startedTask,
                endedTask,
                liveRoomsTask,
                occupiedRoomsTask,
                stageOutcomesTask,
                alertsTask,
                outboxTask);
        }
        catch (PlatformMetricsUnavailableException ex)
        {
            // Await the rest so no task is left to fault unobserved, then report the one thing
            // that is actually known: monitoring could not be read. Not that anything is down.
            await SwallowAsync(targetsTask, workersTask, lagTask, pendingTask, consumersTask,
                deadLetterTask, latencyTask, startedTask, endedTask, liveRoomsTask,
                occupiedRoomsTask, stageOutcomesTask, alertsTask, outboxTask);

            _logger.LogWarning(ex, "Platform metrics source unreachable for the admin health screen");
            // The outbox lives in this service's own database, so it is still worth reporting
            // while Prometheus is down.
            return Unavailable(observedAt, ex.Message) with
            {
                OutboxDeadLetters = outboxTask.IsCompletedSuccessfully ? outboxTask.Result : null,
                GrafanaEmbedPath = _options.GrafanaEmbedPath,
            };
        }

        var streamGroups = BuildStreamGroups(
            await lagTask,
            await pendingTask,
            await consumersTask);

        return new AdminPlatformHealthResponse(
            MonitoringAvailable: true,
            MonitoringUnavailableReason: null,
            ObservedAt: observedAt,
            Targets: BuildTargets(await targetsTask),
            Workers: BuildWorkers(await workersTask),
            StreamGroups: streamGroups,
            DeadLetters: BuildDeadLetters(await deadLetterTask),
            StageLatencies: BuildStageLatencies(await latencyTask),
            Alerts: (await alertsTask)
                .Select(a => new AdminHealthAlert(a.Name, a.Severity, a.State, a.Summary, a.ActiveSince))
                .OrderBy(a => a.Severity, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Warnings: warnings.OrderBy(w => w, StringComparer.Ordinal).ToList())
        {
            Meetings = BuildMeetings(
                await startedTask,
                await endedTask,
                await liveRoomsTask,
                await occupiedRoomsTask),
            StageOutcomes = BuildStageOutcomes(await stageOutcomesTask),
            OutboxDeadLetters = await outboxTask,
            GrafanaEmbedPath = _options.GrafanaEmbedPath,
        };
    }

    private static AdminPlatformHealthResponse Unavailable(DateTime observedAt, string reason) =>
        new(
            MonitoringAvailable: false,
            MonitoringUnavailableReason: reason,
            ObservedAt: observedAt,
            Targets: [],
            Workers: [],
            StreamGroups: [],
            DeadLetters: [],
            StageLatencies: [],
            Alerts: [],
            Warnings: []);

    private async Task<IReadOnlyList<PlatformMetricSample>> QueryAsync(
        string expression,
        string section,
        ConcurrentBag<string> warnings,
        CancellationToken ct)
    {
        try
        {
            return await _metrics.QueryAsync(expression, ct);
        }
        catch (PlatformMetricsUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The store answered but this query did not work — a renamed series, a bad
            // expression. That section comes back empty and says why, rather than reading as
            // "there is nothing there".
            _logger.LogWarning(ex, "Admin health query failed for {Section}", section);
            warnings.Add($"{section} could not be read: {ex.Message}");
            return [];
        }
    }

    private async Task<IReadOnlyList<PlatformAlert>> AlertsAsync(
        ConcurrentBag<string> warnings,
        CancellationToken ct)
    {
        try
        {
            return await _alerts.FiringAlertsAsync(ct);
        }
        catch (Exception ex)
        {
            // Alertmanager down is not Prometheus down: the rest of the screen still stands.
            _logger.LogWarning(ex, "Admin health alert read failed");
            warnings.Add($"firing alerts could not be read: {ex.Message}");
            return [];
        }
    }

    private async Task<AdminHealthOutboxDeadLetters?> OutboxAsync(
        ConcurrentBag<string> warnings,
        CancellationToken ct)
    {
        try
        {
            var (count, oldestAt) = await _outbox.ReadAsync(ct);
            return new AdminHealthOutboxDeadLetters(count, oldestAt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Admin health outbox dead-letter read failed");
            warnings.Add($"outbox dead letters could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The headline. Null only when the counters have no series at all, so "no data yet" does not
    /// read as "no meetings".
    /// </summary>
    internal static AdminHealthMeetingOutcomes? BuildMeetings(
        IReadOnlyList<PlatformMetricSample> started,
        IReadOnlyList<PlatformMetricSample> ended,
        IReadOnlyList<PlatformMetricSample> liveRooms,
        IReadOnlyList<PlatformMetricSample> occupiedRooms)
    {
        if (started.Count == 0 && ended.Count == 0 && liveRooms.Count == 0)
        {
            return null;
        }

        long reachedLive = 0, endedNormally = 0, endedAbandoned = 0, failed = 0;
        foreach (var sample in ended)
        {
            var count = Count(sample.Value);
            if (string.Equals(sample.Label("reached_live"), "true", StringComparison.Ordinal))
            {
                reachedLive += count;
                if (string.Equals(sample.Label("end_reason"), "abandoned", StringComparison.Ordinal))
                {
                    endedAbandoned += count;
                }
                else
                {
                    endedNormally += count;
                }
            }
            else
            {
                failed += count;
            }
        }

        var endedTotal = reachedLive + failed;
        return new AdminHealthMeetingOutcomes(
            Window: "24h",
            Started: started.Sum(s => Count(s.Value)),
            Ended: endedTotal,
            ReachedLive: reachedLive,
            EndedNormally: endedNormally,
            EndedAbandoned: endedAbandoned,
            Failed: failed,
            SuccessRate: endedTotal == 0 ? null : (double)reachedLive / endedTotal,
            LiveRooms: liveRooms.Count == 0 ? null : Count(liveRooms[0].Value),
            OccupiedRooms: occupiedRooms.Count == 0 ? null : Count(occupiedRooms[0].Value));
    }

    internal static List<AdminHealthStageOutcome> BuildStageOutcomes(
        IReadOnlyList<PlatformMetricSample> samples)
    {
        var byStage = samples
            .Where(s => s.Label("stage").Length > 0)
            .GroupBy(s => s.Label("stage"), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        return byStage.Keys
            .OrderBy(stage => Array.IndexOf(PipelineStages, stage) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(stage => stage, StringComparer.Ordinal)
            .Select(stage =>
            {
                long ok = 0, failedAttempts = 0, deadLettered = 0;
                foreach (var sample in byStage[stage])
                {
                    var count = Count(sample.Value);
                    switch (sample.Label("outcome"))
                    {
                        case "ok":
                            ok += count;
                            break;
                        // Parked after its retries were spent. Each of those retries is already an
                        // "error", so counting this as a failure too would double it.
                        case "dead_letter":
                            deadLettered += count;
                            break;
                        default:
                            failedAttempts += count;
                            break;
                    }
                }

                var attempts = ok + failedAttempts;
                return new AdminHealthStageOutcome(
                    stage,
                    ok,
                    failedAttempts,
                    deadLettered,
                    attempts == 0 ? null : (double)ok / attempts);
            })
            .ToList();
    }

    /// <summary>
    /// increase() extrapolates to the window edges, so a counter that moved by 3 can read 3.07.
    /// These are counts of meetings; they are shown as whole numbers.
    /// </summary>
    private static long Count(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : (long)Math.Round(value);

    private static async Task SwallowAsync(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task;
            }
            catch
            {
                // Already reported through the exception that won the race.
            }
        }
    }

    private static List<AdminHealthTarget> BuildTargets(IReadOnlyList<PlatformMetricSample> samples) =>
        samples
            .Select(s => new AdminHealthTarget(s.Label("job"), s.Label("instance"), s.Value >= 1))
            // Down first: the reason to open this screen is at the top of the list without
            // anybody scrolling for it.
            .OrderBy(t => t.IsUp)
            .ThenBy(t => t.Job, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Instance, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<AdminHealthWorker> BuildWorkers(IReadOnlyList<PlatformMetricSample> samples) =>
        samples
            .Select(s => new AdminHealthWorker(WorkerNameFromKey(s.Label("key")), (int)s.Value))
            .Where(w => w.Worker.Length > 0)
            .OrderBy(w => w.Replicas)
            .ThenBy(w => w.Worker, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// <c>warptalk:worker:heartbeat:stt:*</c> → <c>stt</c>. The label carries the glob the
    /// exporter counted, not a worker name.
    /// </summary>
    private static string WorkerNameFromKey(string key)
    {
        if (!key.StartsWith(HeartbeatKeyPrefix, StringComparison.Ordinal)) return string.Empty;
        return key[HeartbeatKeyPrefix.Length..].TrimEnd('*').TrimEnd(':');
    }

    private static List<AdminHealthStreamGroup> BuildStreamGroups(
        IReadOnlyList<PlatformMetricSample> lag,
        IReadOnlyList<PlatformMetricSample> pending,
        IReadOnlyList<PlatformMetricSample> consumers)
    {
        var pendingByKey = ToLookup(pending);
        var consumersByKey = ToLookup(consumers);

        return lag
            .Select(s =>
            {
                var key = (s.Label("stream"), s.Label("group"));
                return new AdminHealthStreamGroup(
                    key.Item1,
                    key.Item2,
                    (long)s.Value,
                    pendingByKey.TryGetValue(key, out var p) ? (long)p : 0,
                    consumersByKey.TryGetValue(key, out var c) ? (int)c : 0);
            })
            .Where(g => g.Stream.Length > 0 && g.Group.Length > 0)
            .OrderByDescending(g => g.Lag)
            .ThenBy(g => g.Stream, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Group, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<(string, string), double> ToLookup(
        IReadOnlyList<PlatformMetricSample> samples)
    {
        var map = new Dictionary<(string, string), double>();
        foreach (var sample in samples)
        {
            map[(sample.Label("stream"), sample.Label("group"))] = sample.Value;
        }

        return map;
    }

    private static List<AdminHealthDeadLetter> BuildDeadLetters(
        IReadOnlyList<PlatformMetricSample> samples) =>
        samples
            .Select(s => new AdminHealthDeadLetter(s.Label("stream"), (long)s.Value))
            .Where(d => d.Stream.Length > 0)
            .OrderByDescending(d => d.Length)
            .ThenBy(d => d.Stream, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<AdminHealthStageLatency> BuildStageLatencies(
        IReadOnlyList<PlatformMetricSample> samples) =>
        samples
            .Select(s => new AdminHealthStageLatency(
                s.Label("stage"),
                // histogram_quantile returns NaN when the window holds too few observations to
                // place a quantile. Null says "not enough data"; 0 would say "instant".
                double.IsNaN(s.Value) || double.IsInfinity(s.Value) ? null : s.Value))
            .Where(s => s.Stage.Length > 0)
            .OrderByDescending(s => s.P95Ms ?? -1)
            .ThenBy(s => s.Stage, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

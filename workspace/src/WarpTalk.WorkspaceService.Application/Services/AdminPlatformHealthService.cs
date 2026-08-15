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

    private readonly IPlatformMetricsSource _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AdminPlatformHealthService> _logger;

    public AdminPlatformHealthService(
        IPlatformMetricsSource metrics,
        TimeProvider timeProvider,
        ILogger<AdminPlatformHealthService> logger)
    {
        _metrics = metrics;
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
        var alertsTask = AlertsAsync(warnings, ct);

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
                alertsTask);
        }
        catch (PlatformMetricsUnavailableException ex)
        {
            // Await the rest so no task is left to fault unobserved, then report the one thing
            // that is actually known: monitoring could not be read. Not that anything is down.
            await SwallowAsync(targetsTask, workersTask, lagTask, pendingTask, consumersTask,
                deadLetterTask, latencyTask, alertsTask);

            _logger.LogWarning(ex, "Platform metrics source unreachable for the admin health screen");
            return Unavailable(observedAt, ex.Message);
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
            Warnings: warnings.OrderBy(w => w, StringComparer.Ordinal).ToList());
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
            return await _metrics.ActiveAlertsAsync(ct);
        }
        catch (PlatformMetricsUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Admin health alert read failed");
            warnings.Add($"active alerts could not be read: {ex.Message}");
            return [];
        }
    }

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

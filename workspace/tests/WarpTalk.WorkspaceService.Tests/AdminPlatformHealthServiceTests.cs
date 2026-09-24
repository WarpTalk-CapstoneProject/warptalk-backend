using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Services;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class AdminPlatformHealthServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 16, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A clock that does not move, so ObservedAt is assertable.</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// Answers by query text, which is what makes the query constants worth having: a test can
    /// aim a fixture at exactly one section.
    /// </summary>
    private sealed class FakeMetricsSource : IPlatformMetricsSource, IPlatformAlertSource
    {
        private readonly Dictionary<string, IReadOnlyList<PlatformMetricSample>> _byQuery;
        private readonly IReadOnlyList<PlatformAlert> _alerts;

        public Exception? ThrowForEverything { get; init; }
        public string? ThrowForQuery { get; init; }

        public FakeMetricsSource(
            Dictionary<string, IReadOnlyList<PlatformMetricSample>>? byQuery = null,
            IReadOnlyList<PlatformAlert>? alerts = null)
        {
            _byQuery = byQuery ?? new Dictionary<string, IReadOnlyList<PlatformMetricSample>>();
            _alerts = alerts ?? [];
        }

        public Task<IReadOnlyList<PlatformMetricSample>> QueryAsync(
            string expression, CancellationToken ct)
        {
            if (ThrowForEverything is { } fatal) throw fatal;
            if (expression == ThrowForQuery)
            {
                throw new InvalidOperationException($"unknown metric in '{expression}'");
            }

            return Task.FromResult(
                _byQuery.TryGetValue(expression, out var samples) ? samples : []);
        }

        public Exception? ThrowForAlerts { get; init; }

        public Task<IReadOnlyList<PlatformAlert>> FiringAlertsAsync(CancellationToken ct)
        {
            if (ThrowForAlerts is { } broken) throw broken;
            if (ThrowForEverything is { } fatal) throw fatal;
            return Task.FromResult(_alerts);
        }
    }

    private sealed class FakeOutbox(long count, DateTime? oldestAt, Exception? failure = null) : IOutboxDeadLetterReader
    {
        public Task<(long Count, DateTime? OldestAt)> ReadAsync(CancellationToken ct) =>
            failure is null ? Task.FromResult((count, oldestAt)) : Task.FromException<(long, DateTime?)>(failure);
    }

    private static AdminPlatformHealthService Build(
        IPlatformMetricsSource source,
        IOutboxDeadLetterReader? outbox = null,
        string? grafanaEmbedPath = null) =>
        new(
            source,
            (IPlatformAlertSource)source,
            outbox ?? new FakeOutbox(0, null),
            new PlatformHealthOptions { GrafanaEmbedPath = grafanaEmbedPath },
            new FixedTimeProvider(),
            Substitute.For<ILogger<AdminPlatformHealthService>>());

    private static PlatformMetricSample Sample(double value, params (string Key, string Value)[] labels) =>
        new(labels.ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal), value);

    private static IPlatformMetricsSource SourceFor(
        Dictionary<string, IReadOnlyList<PlatformMetricSample>> byQuery,
        IReadOnlyList<PlatformAlert>? alerts = null) => new FakeMetricsSource(byQuery, alerts);

    [Fact]
    public async Task AnUnreachableStoreIsReportedAsUnreadable_NotAsAnOutage()
    {
        // The distinction this asserts is the whole reason the flag exists. A screen that renders
        // an unreachable Prometheus as empty lists is indistinguishable from one reporting that
        // every worker is gone and every target is down.
        var source = new FakeMetricsSource
        {
            ThrowForEverything =
                new PlatformMetricsUnavailableException("The metrics store could not be reached."),
        };

        var result = await Build(source).ReadAsync();

        Assert.False(result.MonitoringAvailable);
        Assert.Equal("The metrics store could not be reached.", result.MonitoringUnavailableReason);
        Assert.Empty(result.Targets);
        Assert.Empty(result.Workers);
        // No warnings either: one unreachable store is one fact, not eight failed sections.
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task OneBrokenQueryLeavesTheRestOfTheScreenIntact_AndSaysWhichOneBroke()
    {
        var source = new FakeMetricsSource(new()
        {
            [AdminPlatformHealthService.TargetsQuery] =
                [Sample(1, ("job", "postgres"), ("instance", "postgres-exporter:9187"))],
        })
        {
            ThrowForQuery = AdminPlatformHealthService.WorkersQuery,
        };

        var result = await Build(source).ReadAsync();

        Assert.True(result.MonitoringAvailable);
        Assert.Empty(result.Workers);
        Assert.Contains(result.Warnings, w => w.Contains("worker heartbeats", StringComparison.Ordinal));
        Assert.NotEmpty(result.Targets);
    }

    [Fact]
    public async Task DownTargetsSortFirst()
    {
        var result = await Build(SourceFor(new()
        {
            [AdminPlatformHealthService.TargetsQuery] =
            [
                Sample(1, ("job", "postgres"), ("instance", "postgres-exporter:9187")),
                Sample(0, ("job", "rabbitmq"), ("instance", "rabbitmq:15692")),
                Sample(1, ("job", "redis"), ("instance", "redis-exporter:9121")),
            ],
        })).ReadAsync();

        Assert.Equal("rabbitmq", result.Targets[0].Job);
        Assert.False(result.Targets[0].IsUp);
        Assert.All(result.Targets.Skip(1), t => Assert.True(t.IsUp));
    }

    [Fact]
    public async Task WorkerNamesComeOutOfTheHeartbeatGlob()
    {
        // The exporter labels these with the pattern it counted, not with a worker name.
        var result = await Build(SourceFor(new()
        {
            [AdminPlatformHealthService.WorkersQuery] =
            [
                Sample(2, ("key", "warptalk:worker:heartbeat:stt:*")),
                Sample(0, ("key", "warptalk:worker:heartbeat:livekit_ingress:*")),
            ],
        })).ReadAsync();

        // Zero replicas first — that is the row worth reading.
        Assert.Equal("livekit_ingress", result.Workers[0].Worker);
        Assert.Equal(0, result.Workers[0].Replicas);
        Assert.Equal("stt", result.Workers[1].Worker);
        Assert.Equal(2, result.Workers[1].Replicas);
    }

    [Fact]
    public async Task StreamGroupsJoinLagPendingAndConsumersOnStreamAndGroup()
    {
        var result = await Build(SourceFor(new()
        {
            [AdminPlatformHealthService.LagQuery] =
            [
                Sample(4, ("stream", "stt:results"), ("group", "translate-workers")),
                Sample(120, ("stream", "stt:results"), ("group", "billing-stt-workers")),
            ],
            [AdminPlatformHealthService.PendingQuery] =
            [
                Sample(9, ("stream", "stt:results"), ("group", "billing-stt-workers")),
            ],
            [AdminPlatformHealthService.ConsumersQuery] =
            [
                Sample(1, ("stream", "stt:results"), ("group", "translate-workers")),
                Sample(0, ("stream", "stt:results"), ("group", "billing-stt-workers")),
            ],
        })).ReadAsync();

        var worst = result.StreamGroups[0];
        Assert.Equal("billing-stt-workers", worst.Group);
        Assert.Equal(120, worst.Lag);
        Assert.Equal(9, worst.Pending);
        Assert.Equal(0, worst.Consumers);

        // A group present in lag but absent from pending is at zero pending, not missing.
        var healthy = result.StreamGroups[1];
        Assert.Equal("translate-workers", healthy.Group);
        Assert.Equal(0, healthy.Pending);
        Assert.Equal(1, healthy.Consumers);
    }

    [Fact]
    public async Task AStageWithTooFewObservationsIsNull_NotZero()
    {
        // histogram_quantile returns NaN over a window that holds too few samples to place a
        // quantile. Reporting that as 0 ms would put "instant" next to a stage nobody measured.
        var result = await Build(SourceFor(new()
        {
            [AdminPlatformHealthService.StageLatencyQuery] =
            [
                Sample(double.NaN, ("stage", "tts")),
                Sample(2400, ("stage", "stt")),
            ],
        })).ReadAsync();

        Assert.Equal("stt", result.StageLatencies[0].Stage);
        Assert.Equal(2400, result.StageLatencies[0].P95Ms);
        Assert.Equal("tts", result.StageLatencies[1].Stage);
        Assert.Null(result.StageLatencies[1].P95Ms);
    }

    [Fact]
    public async Task AlertsCarryTheirSeverityAndWhenTheyStarted()
    {
        var activeAt = new DateTime(2026, 8, 16, 8, 42, 0, DateTimeKind.Utc);
        var result = await Build(SourceFor(
            new(),
            [
                new PlatformAlert("WarpTalkAiStreamLag", "warning", "firing", "behind on stt:results", activeAt),
                new PlatformAlert("WarpTalkAiWorkerMissing", "critical", "firing", "stt heartbeat missing", activeAt),
            ])).ReadAsync();

        Assert.Equal("WarpTalkAiWorkerMissing", result.Alerts[0].Name);
        Assert.Equal("critical", result.Alerts[0].Severity);
        Assert.Equal(activeAt, result.Alerts[0].ActiveSince);
    }

    [Fact]
    public async Task ObservedAtComesFromTheClock_NotFromTheStore()
    {
        var result = await Build(SourceFor(new())).ReadAsync();

        Assert.Equal(Now.UtcDateTime, result.ObservedAt);
        Assert.Equal(
            "2026-08-16T09:00:00",
            result.ObservedAt.ToString("s", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task MeetingSuccessRateIsReachedLiveOverEnded_WithAbandonedAfterLiveCountedAsSuccess()
    {
        var result = await Build(SourceFor(new()
        {
            [AdminPlatformHealthService.MeetingsStartedQuery] = [Sample(12.04)],
            [AdminPlatformHealthService.MeetingsEndedQuery] =
            [
                Sample(6.1, ("end_reason", "host"), ("reached_live", "true")),
                Sample(2, ("end_reason", "abandoned"), ("reached_live", "true")),
                Sample(1.9, ("end_reason", "abandoned"), ("reached_live", "false")),
                Sample(0, ("end_reason", "expired"), ("reached_live", "false")),
            ],
            [AdminPlatformHealthService.LiveRoomsQuery] = [Sample(3)],
            [AdminPlatformHealthService.OccupiedRoomsQuery] = [Sample(2)],
        })).ReadAsync();

        var meetings = Assert.IsType<WarpTalk.WorkspaceService.Application.DTOs.Admin.AdminHealthMeetingOutcomes>(result.Meetings);
        Assert.Equal(12, meetings.Started);
        Assert.Equal(10, meetings.Ended);
        Assert.Equal(8, meetings.ReachedLive);
        Assert.Equal(6, meetings.EndedNormally);
        Assert.Equal(2, meetings.EndedAbandoned);
        Assert.Equal(2, meetings.Failed);
        Assert.Equal(0.8, meetings.SuccessRate!.Value, 3);
        Assert.Equal(3, meetings.LiveRooms);
        Assert.Equal(2, meetings.OccupiedRooms);
    }

    [Fact]
    public async Task NoMeetingSeriesIsNull_NotZeroMeetings()
    {
        var result = await Build(SourceFor(new())).ReadAsync();

        Assert.Null(result.Meetings);
    }

    [Fact]
    public async Task NothingEndedMeansNoRate_NotZeroPercent()
    {
        var result = await Build(SourceFor(new()
        {
            [AdminPlatformHealthService.MeetingsStartedQuery] = [Sample(2)],
        })).ReadAsync();

        Assert.NotNull(result.Meetings);
        Assert.Null(result.Meetings!.SuccessRate);
    }

    [Fact]
    public async Task StageOutcomesComeInPipelineOrder_AndDeadLettersAreNotCountedTwice()
    {
        var result = await Build(SourceFor(new()
        {
            [AdminPlatformHealthService.StageOutcomesQuery] =
            [
                Sample(90, ("stage", "tts"), ("outcome", "ok")),
                Sample(10, ("stage", "tts"), ("outcome", "vendor_error")),
                Sample(40, ("stage", "stt"), ("outcome", "ok")),
                Sample(5, ("stage", "stt"), ("outcome", "error")),
                Sample(5, ("stage", "stt"), ("outcome", "timeout")),
                Sample(1, ("stage", "stt"), ("outcome", "dead_letter")),
                Sample(0, ("stage", "translation"), ("outcome", "ok")),
            ],
        })).ReadAsync();

        Assert.Equal(["stt", "translation", "tts"], result.StageOutcomes.Select(s => s.Stage));
        Assert.Equal(10, result.StageOutcomes[0].Failed);
        Assert.Equal(1, result.StageOutcomes[0].DeadLettered);
        Assert.Equal(0.8, result.StageOutcomes[0].SuccessRate!.Value, 3);
        Assert.Null(result.StageOutcomes[1].SuccessRate);
        Assert.Equal(0.9, result.StageOutcomes[2].SuccessRate!.Value, 3);
    }

    [Fact]
    public async Task AnUnreachableAlertmanagerIsAWarning_NotAMonitoringOutage()
    {
        var source = new FakeMetricsSource(new()
        {
            [AdminPlatformHealthService.TargetsQuery] = [Sample(1, ("job", "redis"), ("instance", "redis:9121"))],
        })
        {
            ThrowForAlerts = new HttpRequestException("connection refused"),
        };

        var result = await Build(source).ReadAsync();

        Assert.True(result.MonitoringAvailable);
        Assert.Empty(result.Alerts);
        Assert.Contains(result.Warnings, w => w.Contains("firing alerts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OutboxDeadLettersAreReported_EvenWhenPrometheusIsUnreachable()
    {
        var oldest = new DateTime(2026, 9, 20, 3, 0, 0, DateTimeKind.Utc);
        var source = new FakeMetricsSource
        {
            ThrowForEverything = new PlatformMetricsUnavailableException("down"),
        };

        var result = await Build(source, new FakeOutbox(4, oldest), "/grafana").ReadAsync();

        Assert.False(result.MonitoringAvailable);
        Assert.Equal(4, result.OutboxDeadLetters!.Count);
        Assert.Equal(oldest, result.OutboxDeadLetters.OldestAt);
        Assert.Equal("/grafana", result.GrafanaEmbedPath);
    }

    [Fact]
    public async Task AnUnreadableOutboxIsNull_WithAWarning()
    {
        var result = await Build(
            SourceFor(new()),
            new FakeOutbox(0, null, new InvalidOperationException("db down"))).ReadAsync();

        Assert.Null(result.OutboxDeadLetters);
        Assert.Contains(result.Warnings, w => w.Contains("outbox dead letters", StringComparison.Ordinal));
    }
}

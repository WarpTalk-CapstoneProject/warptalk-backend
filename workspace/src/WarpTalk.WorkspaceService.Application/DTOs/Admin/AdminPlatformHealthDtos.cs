using System;
using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Application.DTOs.Admin;

/// <summary>
/// What the platform admin System Health screen reports.
///
/// Every number here is read back out of Prometheus, which is the only process that already
/// scrapes all twelve exporters. Nothing on this screen is computed by asking a service whether
/// it feels well: a service that has lost its Redis consumer group answers its own health check
/// with a 200 and has done exactly that in production (WT-402).
/// </summary>
/// <param name="MonitoringAvailable">
/// FALSE means we could not read monitoring — NOT that the platform is down. The distinction is
/// the whole point of the flag: a page that renders an unreachable Prometheus as a wall of zeroes
/// reports a total outage every time the monitoring host restarts.
/// </param>
/// <param name="Warnings">
/// Individual queries that failed while Prometheus itself answered. The sections they feed come
/// back empty, and an empty section with no warning means genuinely no data.
/// </param>
public sealed record AdminPlatformHealthResponse(
    bool MonitoringAvailable,
    string? MonitoringUnavailableReason,
    DateTime ObservedAt,
    IReadOnlyList<AdminHealthTarget> Targets,
    IReadOnlyList<AdminHealthWorker> Workers,
    IReadOnlyList<AdminHealthStreamGroup> StreamGroups,
    IReadOnlyList<AdminHealthDeadLetter> DeadLetters,
    IReadOnlyList<AdminHealthStageLatency> StageLatencies,
    IReadOnlyList<AdminHealthAlert> Alerts,
    IReadOnlyList<string> Warnings);

/// <summary>One Prometheus scrape target: an exporter or an application job.</summary>
public sealed record AdminHealthTarget(string Job, string Instance, bool IsUp);

/// <summary>
/// How many heartbeat keys a worker class currently holds. Zero replicas is the condition the
/// WarpTalkAiWorkerMissing alert already watches for.
/// </summary>
public sealed record AdminHealthWorker(string Worker, int Replicas);

/// <summary>
/// A Redis Stream consumer group as the exporter discovered it.
/// </summary>
/// <param name="Consumers">
/// Consumer names Redis has ever seen on this group, not readers attached right now — Redis keeps
/// a consumer registered after its process exits. Zero is the meaningful value: a group with a
/// producer and nothing ever wired to read it.
/// </param>
public sealed record AdminHealthStreamGroup(
    string Stream,
    string Group,
    long Lag,
    long Pending,
    int Consumers);

public sealed record AdminHealthDeadLetter(string Stream, long Length);

/// <summary>
/// p95 for one pipeline stage over the reporting window, in milliseconds. Null when the stage has
/// observations but too few to place a quantile, which is not the same as fast.
/// </summary>
public sealed record AdminHealthStageLatency(string Stage, double? P95Ms);

public sealed record AdminHealthAlert(
    string Name,
    string Severity,
    string State,
    string? Summary,
    DateTime? ActiveSince);

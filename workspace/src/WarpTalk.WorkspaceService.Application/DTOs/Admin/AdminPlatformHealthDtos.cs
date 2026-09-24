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
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// The headline: how many meetings worked over the last 24 hours. Null when the counters have
    /// no series yet (a fresh deploy, or the translation-room service not exporting), which is not
    /// the same as zero meetings.
    /// </summary>
    public AdminHealthMeetingOutcomes? Meetings { get; init; }

    /// <summary>STT, MT and TTS attempt outcomes over the last hour, one row per stage.</summary>
    public IReadOnlyList<AdminHealthStageOutcome> StageOutcomes { get; init; } = [];

    /// <summary>
    /// Workspace outbox events that exhausted their retries. Null when it could not be read. The
    /// admin page that listed them was retired; the count stays here so they cannot pile up unseen.
    /// </summary>
    public AdminHealthOutboxDeadLetters? OutboxDeadLetters { get; init; }

    /// <summary>
    /// Same-origin path the embedded Grafana is served under (e.g. <c>/grafana</c>), or null where
    /// no Grafana is published — local and CI, where the screen simply leaves the charts out.
    /// </summary>
    public string? GrafanaEmbedPath { get; init; }
}

/// <summary>
/// Meeting outcomes over a window. "Reached live" means at least two people joined AND a caption
/// was delivered to the room; a meeting that ended without both counts as failed.
/// </summary>
/// <param name="EndedNormally">Reached live and the host ended it.</param>
/// <param name="EndedAbandoned">Reached live, then everybody left and the sweep ended it.</param>
/// <param name="Failed">Ended without ever reaching live.</param>
/// <param name="SuccessRate">ReachedLive / ended, 0..1; null when nothing ended in the window.</param>
/// <param name="LiveRooms">Rooms in a live status at the last sweep (≤ 12 min old); null if unknown.</param>
/// <param name="OccupiedRooms">Of those, rooms with at least one person in them.</param>
public sealed record AdminHealthMeetingOutcomes(
    string Window,
    long Started,
    long Ended,
    long ReachedLive,
    long EndedNormally,
    long EndedAbandoned,
    long Failed,
    double? SuccessRate,
    long? LiveRooms,
    long? OccupiedRooms);

/// <param name="Failed">error + timeout + vendor_error attempts.</param>
/// <param name="SuccessRate">Ok / (Ok + Failed), 0..1; null with no attempts in the window.</param>
public sealed record AdminHealthStageOutcome(string Stage, long Ok, long Failed, long DeadLettered, double? SuccessRate);

public sealed record AdminHealthOutboxDeadLetters(long Count, DateTime? OldestAt);

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

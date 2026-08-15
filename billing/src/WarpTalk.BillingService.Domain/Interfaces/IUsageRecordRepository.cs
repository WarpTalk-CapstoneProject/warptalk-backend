using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

/// <summary>
/// One member's share of a workspace's credit spend.
///
/// WT-413. Deliberately keyed by user id and NOT by name: this service has no user directory —
/// it holds an IWorkspaceClient and an INotificationClient and nothing that resolves a person —
/// and the caller already has the workspace member list on screen. Inventing a gRPC dependency
/// to decorate an aggregate would couple billing to auth for a label.
/// </summary>
public sealed record WorkspaceMemberUsage(
    Guid UserId,
    int CreditsConsumed,
    int RecordCount,
    DateTime? LastUsedAt);

public interface IUsageRecordRepository : IGenericRepository<UsageRecord>
{
    /// <summary>
    /// Credit spend per member for one workspace, newest activity first.
    ///
    /// Aggregated in SQL rather than in memory: a busy workspace accumulates a usage row per
    /// translated segment (474 rows across three users in the demo workspace alone after a few
    /// days), and this is a dashboard read that must not scale with segment count.
    ///
    /// Rows with no user attribution are excluded rather than bucketed into an "unknown" row.
    /// Production has none — 474 of 474 carry a user id — and a silent catch-all bucket would
    /// hide the day that stops being true.
    /// </summary>
    Task<IReadOnlyList<WorkspaceMemberUsage>> GetUsageByMemberAsync(
        Guid workspaceId,
        DateTime? from,
        DateTime? to,
        CancellationToken ct = default);
}

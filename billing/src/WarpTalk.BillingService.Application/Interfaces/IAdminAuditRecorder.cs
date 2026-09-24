using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// Records a privileged billing action in the platform audit log, which the workspace service owns.
///
/// Billing already had a bus, and <c>AdminAuditPublisher</c> was written for exactly this — but it
/// is fire-and-forget: it logs a failed publish and returns, by which time the credit grant it
/// describes is committed. The admin workspace page's rule is that an action that cannot be
/// recorded does not happen, so billing uses the same synchronous gRPC contract auth and
/// translation-room use (<c>AdminAuditService.RecordAdminAction</c>) and records BEFORE it saves.
/// </summary>
public interface IAdminAuditRecorder
{
    /// <param name="action">An <c>AdminAuditWorkspaceActions</c> value.</param>
    /// <param name="entityType">An <c>AdminAuditEntityTypes</c> value.</param>
    /// <param name="workspaceId">The workspace the action was taken on; it is what puts the entry on
    /// that workspace's timeline.</param>
    /// <param name="succeeded">False writes a <c>failed</c> entry — used when the change was recorded
    /// and then failed to commit, so the trail never claims a change that did not happen.</param>
    /// <param name="correlationId">De-duplication key; the store ignores a repeat.</param>
    Task<Result> RecordAsync(
        string action,
        string entityType,
        Guid? entityId,
        Guid workspaceId,
        Guid actorId,
        string reason,
        string correlationId,
        IReadOnlyDictionary<string, string?>? beforeSummary = null,
        IReadOnlyDictionary<string, string?>? afterSummary = null,
        bool succeeded = true,
        CancellationToken ct = default);
}

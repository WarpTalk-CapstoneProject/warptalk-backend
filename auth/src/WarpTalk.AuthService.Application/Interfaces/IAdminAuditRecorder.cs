using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Interfaces;

/// <summary>
/// Records a privileged action taken against a platform account, in the store the audit screen
/// reads.
///
/// Deliberately returns a <see cref="Result"/> rather than being fire-and-forget. Auth has no bus,
/// so this goes to the workspace service over gRPC — and the point of choosing a synchronous
/// transport is that the caller can decide what to do when the record fails. It refuses the
/// action. An unaudited session revocation is the exact outcome this feature waited on rather
/// than shipping.
/// </summary>
public interface IAdminAuditRecorder
{
    /// <param name="correlationId">
    /// De-duplication key. The store ignores a repeat of the same (source, correlation, action,
    /// entity), so a retry after a timeout records once — which matters here because a timeout
    /// cannot be distinguished from a failure by the caller.
    /// </param>
    Task<Result> RecordAsync(
        string action,
        Guid entityId,
        Guid actorId,
        string reason,
        string correlationId,
        Guid? workspaceId,
        IReadOnlyDictionary<string, string?>? beforeSummary = null,
        IReadOnlyDictionary<string, string?>? afterSummary = null,
        CancellationToken ct = default);

    /// <summary>
    /// The same guarantee for a subject that is not a user account (G10: a staff role, an
    /// invitation), or when the entry should name what the subject was called at the time.
    /// </summary>
    Task<Result> RecordSubjectAsync(AdminAuditSubjectRecord record, CancellationToken ct = default);
}

/// <param name="EntityType">See AdminAuditEntityTypes.</param>
/// <param name="ActorId">
/// Normally the admin. For an invitation accepted at sign-in, the invitee: nobody else acted.
/// </param>
public sealed record AdminAuditSubjectRecord(
    string Action,
    string EntityType,
    Guid? EntityId,
    string? EntityLabel,
    Guid ActorId,
    string Reason,
    string CorrelationId,
    IReadOnlyDictionary<string, string?>? BeforeSummary = null,
    IReadOnlyDictionary<string, string?>? AfterSummary = null);

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// Records a privileged catalog change in the platform audit log, which the workspace service owns.
///
/// This service has no message bus, and that used to be the stated reason the language catalog was
/// read-only: a platform-wide switch that leaves no trace of who threw it. Auth solved the same
/// problem with a synchronous gRPC call (<c>AdminAuditService.RecordAdminAction</c>) and this is the
/// same transport. It returns a <see cref="Result"/> rather than being fire-and-forget because the
/// caller's answer to a failed record is to abandon the change (WT-691).
/// </summary>
public interface IAdminAuditRecorder
{
    /// <param name="entityType">An <c>AdminAuditEntityTypes</c> value.</param>
    /// <param name="correlationId">De-duplication key; the store ignores a repeat.</param>
    Task<Result> RecordAsync(
        string action,
        string entityType,
        Guid actorId,
        string reason,
        string correlationId,
        IReadOnlyDictionary<string, string?>? beforeSummary = null,
        IReadOnlyDictionary<string, string?>? afterSummary = null,
        CancellationToken ct = default);
}

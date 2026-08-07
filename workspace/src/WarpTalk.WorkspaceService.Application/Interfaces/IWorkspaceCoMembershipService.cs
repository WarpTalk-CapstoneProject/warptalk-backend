using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

/// <summary>
/// WT-335 — "which of these users is the caller allowed to know anything about?"
///
/// Its own interface rather than another method on <see cref="IWorkspaceDirectoryService"/>: that
/// type answers questions about ONE workspace the caller has already named, and every method on it
/// is shaped <c>(workspaceId, userId) -&gt; detail</c>. This asks the inverse and unscoped question —
/// given only a caller, which of these arbitrary user ids are visible to them at all — and its
/// consumer is a privacy filter rather than a lookup.
/// </summary>
public interface IWorkspaceCoMembershipService
{
    /// <summary>
    /// The subset of <paramref name="candidateUserIds"/> that share at least one ACTIVE workspace
    /// with <paramref name="callerUserId"/>.
    ///
    /// Batch in, batch out, on purpose. The caller is the Gateway's presence query, which accepts
    /// up to 500 ids on a hot path; a per-user "do these two share a workspace" call would turn one
    /// presence request into 500 round trips and replace a security defect with a performance one.
    ///
    /// Ids the caller may not see are simply ABSENT from the result. There is no "denied" marker,
    /// because a marker would confirm the account exists — the same leak, one level down.
    /// </summary>
    Task<Result<IReadOnlyList<Guid>>> GetVisibleCoMemberIdsAsync(
        Guid callerUserId,
        IReadOnlyCollection<Guid> candidateUserIds,
        CancellationToken ct = default);
}

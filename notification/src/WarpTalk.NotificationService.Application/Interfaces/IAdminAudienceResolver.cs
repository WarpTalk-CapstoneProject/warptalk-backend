using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;

namespace WarpTalk.NotificationService.Application.Interfaces;

/// <summary>
/// WT-699 / TC4104: who an admin announcement goes to, when the admin did not name them.
///
/// BROADCAST is every active account (AuthService); SEGMENT is every active member of one
/// workspace, the segment id being the workspace id (WorkspaceService). The validator used to
/// refuse both modes outright — "Only SPECIFIC_USERS is supported until a production user/segment
/// resolver is configured" — and this is that resolver.
///
/// Resolved once, at creation, into explicit user ids, so the delivery pipeline (chunked events,
/// per-chunk receipts, Sent/Failed accounting) is the same for every mode and a BROADCAST cannot
/// reach somebody who registers halfway through its delivery.
/// </summary>
public interface IAdminAudienceResolver
{
    Task<Result<IReadOnlyList<Guid>>> ResolveAsync(
        string targetAudienceMode,
        Guid? segmentId,
        CancellationToken ct = default);
}

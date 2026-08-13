using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Domain.Interfaces;

public interface IWorkspaceVerifiedDomainRepository : IGenericRepository<WorkspaceVerifiedDomain>
{
    Task<(List<WorkspaceVerifiedDomain> Items, int TotalCount)> GetPagedVerifiedDomainsAsync(
        Guid workspaceId,
        int page,
        int pageSize,
        bool isDescending = true,
        CancellationToken ct = default);

    /// <summary>
    /// Whether <paramref name="exception"/> is the database refusing a write because another
    /// workspace already holds that domain — as opposed to any other failure, which must still
    /// surface as an error.
    ///
    /// Two callers may check a domain is free and both be told yes; only one INSERT survives.
    /// The loser needs to hear "somebody got there first", not "something broke". Telling those
    /// apart means reading a vendor-specific error code, which belongs to whatever is actually
    /// talking to the database — this repository owns the table and its unique index, so it
    /// answers for them.
    /// </summary>
    bool IsDomainAlreadyClaimedViolation(Exception exception);
}

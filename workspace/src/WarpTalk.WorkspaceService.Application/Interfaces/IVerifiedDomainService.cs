using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Application.DTOs.VerifiedDomain;
using WarpTalk.Shared;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IVerifiedDomainService
{
    /// <summary>
    /// Adds a non-public domain to the workspace's verified domain list.
    /// The domain is trusted and marked verified immediately (business trust model — no DNS challenge).
    /// Only the workspace Owner may call this.
    /// </summary>
    Task<Result<VerifiedDomainDto>> AddDomainAsync(Guid workspaceId, string domain, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the active (non-revoked) verified domains for a workspace.
    /// Accessible to Owner and Admin.
    /// </summary>
    Task<Result<List<VerifiedDomainDto>>> ListDomainsAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Revokes a verified domain. Blocked if it is the last active domain and
    /// <c>RequireVerifiedDomainForInternal</c> is still enabled on the workspace.
    /// Only the workspace Owner may call this.
    /// </summary>
    Task<Result> RevokeDomainAsync(Guid workspaceId, Guid domainId, Guid userId, CancellationToken ct = default);
}

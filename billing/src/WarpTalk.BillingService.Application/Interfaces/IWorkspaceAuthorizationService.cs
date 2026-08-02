using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IWorkspaceAuthorizationService
{
    Task<Result> AuthorizeAsync(Guid workspaceId, Guid userId, string allowedRoles, CancellationToken cancellationToken = default);
}

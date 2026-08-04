using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IWorkspaceInvitationAcceptanceProcessor
{
    Task<Result> ValidateAcceptanceAsync(
        WorkspaceInvitation invitation,
        Guid userId,
        string userEmail,
        CancellationToken ct = default);

    Task<Result> ProcessAcceptanceAsync(
        WorkspaceInvitation invitation,
        Guid userId,
        string userEmail,
        CancellationToken ct = default);
}

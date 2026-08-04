using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared.Interfaces;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface IWorkspaceInvitationEmailComposer
{
    Task<SendEmailResponse> SendInvitationEmailAsync(
        WorkspaceInvitation invitation,
        Workspace workspace,
        string inviterName,
        string roleName,
        string invitationToken,
        CancellationToken ct = default);

    Task<SendEmailResponse> SendJoinRequestApprovedEmailAsync(
        WorkspaceInvitation invitation,
        Workspace workspace,
        CancellationToken ct = default);
}

using System;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Application.Validators;

public static class WorkspaceInvitationValidator
{
    public static void ValidateForMapping(WorkspaceInvitation invitation, string roleName)
    {
        if (invitation == null)
        {
            throw new ArgumentNullException(nameof(invitation));
        }

        if (string.IsNullOrWhiteSpace(roleName))
        {
            throw new InvalidOperationException("Role Name is strictly required when mapping a WorkspaceInvitation to WorkspaceInvitationDto.");
        }
    }
}

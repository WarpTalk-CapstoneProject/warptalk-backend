using System;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Application.Validators;

public static class WorkspaceInvitationValidator
{
    public static void ValidateForMapping(WorkspaceInvitation invitation)
    {
        if (invitation == null)
        {
            throw new ArgumentNullException(nameof(invitation));
        }

        if (invitation.Role == null || string.IsNullOrWhiteSpace(invitation.Role.Name))
        {
            throw new InvalidOperationException("Role and Role Name are strictly required when mapping a WorkspaceInvitation to WorkspaceInvitationDto.");
        }
    }
}

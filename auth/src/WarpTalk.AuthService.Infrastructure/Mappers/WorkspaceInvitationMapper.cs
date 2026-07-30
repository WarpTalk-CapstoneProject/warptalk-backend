using System;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared.Protos;

namespace WarpTalk.AuthService.Infrastructure.Mappers;

public static class WorkspaceInvitationMapper
{
    public static VerifyInvitationResult ToResult(VerifyInvitationTokenResponse response)
    {
        if (!response.IsValid)
        {
            return new VerifyInvitationResult(
                IsValid: false,
                Email: null,
                WorkspaceId: null,
                WorkspaceName: null,
                RoleId: null,
                RoleName: null,
                MembershipType: null,
                ErrorMessage: response.ErrorMessage
            );
        }

        return new VerifyInvitationResult(
            IsValid: true,
            Email: response.Email,
            WorkspaceId: Guid.TryParse(response.WorkspaceId, out var wsId) ? wsId : null,
            WorkspaceName: response.WorkspaceName,
            RoleId: Guid.TryParse(response.RoleId, out var rId) ? rId : null,
            RoleName: response.RoleName,
            MembershipType: response.MembershipType,
            ErrorMessage: null
        );
    }

    public static AcceptInvitationResult ToResult(AcceptInvitationResponse response)
    {
        return new AcceptInvitationResult(
            Success: response.Success,
            ErrorMessage: response.ErrorMessage
        );
    }
}

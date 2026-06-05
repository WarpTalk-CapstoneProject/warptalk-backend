using System;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceMember;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;

namespace WarpTalk.WorkspaceService.Application.Mappers.WorkspaceMember;

public static class WorkspaceMemberMapper
{
    public static Domain.Entities.WorkspaceMember CreateOwnerMember(Guid workspaceId, Guid userId, Guid roleId)
    {
        return new Domain.Entities.WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = roleId,
            Status = WorkspaceMemberStatus.Active.ToString(),
            MembershipType = MembershipType.Internal.ToString(),
            JoinedAt = DateTime.UtcNow
        };
    }

    public static Domain.Entities.WorkspaceMember CreateInvitationMember(Guid workspaceId, Guid userId, Guid roleId, string membershipType)
    {
        return new Domain.Entities.WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = roleId,
            Status = WorkspaceMemberStatus.Active.ToString(),
            MembershipType = membershipType,
            JoinedAt = DateTime.UtcNow
        };
    }

    public static WorkspaceMemberDto ToDto(this Domain.Entities.WorkspaceMember member, string fullName, string email, string? avatarUrl, string roleName)
    {
        return new WorkspaceMemberDto(
            member.Id,
            member.WorkspaceId,
            member.UserId,
            fullName,
            email,
            avatarUrl,
            roleName,
            member.Status,
            member.JoinedAt,
            member.MembershipType
        );
    }
}

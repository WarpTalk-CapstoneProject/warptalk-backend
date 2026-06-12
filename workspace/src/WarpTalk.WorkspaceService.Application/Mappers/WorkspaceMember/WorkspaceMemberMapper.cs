using System;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceMember;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using WarpTalk.WorkspaceService.Domain.Extensions;

namespace WarpTalk.WorkspaceService.Application.Mappers;

public static class WorkspaceMemberMapper
{
    public static WorkspaceMember CreateOwnerMember(Guid workspaceId, Guid userId, Guid roleId, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        return new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = roleId,
            Status = WorkspaceMemberStatus.Active.ToString(),
            MembershipType = MembershipType.Internal.ToString(),
            JoinedAt = now
        };
    }

    public static WorkspaceMember CreateInvitationMember(Guid workspaceId, Guid userId, Guid roleId, string membershipType, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        return new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = roleId,
            Status = WorkspaceMemberStatus.Active.ToString(),
            MembershipType = membershipType,
            JoinedAt = now
        };
    }

    public static WorkspaceMemberDto ToDto(this WorkspaceMember member, string fullName, string email, string? avatarUrl, string roleName)
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

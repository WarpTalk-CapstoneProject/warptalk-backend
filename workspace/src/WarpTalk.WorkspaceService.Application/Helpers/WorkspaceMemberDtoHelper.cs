using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceMember;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Mappers;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Application.Helpers;

public static class WorkspaceMemberDtoHelper
{
    public static async Task<List<WorkspaceMemberDto>> BuildAsync(
        IEnumerable<WorkspaceMember> members,
        IAuthIdentityClient authIdentity,
        CancellationToken ct)
    {
        var memberList = members.ToList();
        var userResults = await Task.WhenAll(memberList.Select(m => authIdentity.GetUserByIdAsync(m.UserId, ct)));
        var userMap = memberList.Zip(userResults, (m, u) => (m.UserId, User: u))
            .ToDictionary(x => x.UserId, x => x.User);

        var distinctRoleIds = memberList.Select(m => m.RoleId).Distinct().ToList();
        var roleResults = await Task.WhenAll(distinctRoleIds.Select(rId => authIdentity.GetRoleByIdAsync(rId, ct)));
        var roleMap = distinctRoleIds.Zip(roleResults, (id, r) => (id, Name: r?.Name ?? "Member"))
            .ToDictionary(x => x.id, x => x.Name);

        return memberList.Select(m =>
        {
            var user = userMap.GetValueOrDefault(m.UserId);
            var fullName = user?.FullName ?? "Unknown";
            var email = user?.Email ?? string.Empty;
            var avatarUrl = user?.AvatarUrl;
            var roleName = roleMap.GetValueOrDefault(m.RoleId, "Member");

            return m.ToDto(fullName, email, avatarUrl, roleName);
        }).ToList();
    }
}

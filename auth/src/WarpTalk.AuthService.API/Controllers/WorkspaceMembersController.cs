using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Controllers;

[ApiController]
[Route("api/v1/workspaces")]
public class WorkspaceMembersController : BaseApiController
{
    private readonly IWorkspaceService _workspaceService;

    public WorkspaceMembersController(IWorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
    }

    [Authorize]
    [HttpGet("{workspaceId:guid}/members")]
    public async Task<Result<PagedResult<WorkspaceMemberDto>>> ListMembers(Guid workspaceId, [FromQuery] GetWorkspacesQuery query, CancellationToken ct)
    {
        return await _workspaceService.ListMembersAsync(workspaceId, query, CurrentUserId, ct);
    }

    [Authorize]
    [HttpDelete("{workspaceId:guid}/members/{userId:guid}")]
    public async Task<Result> RemoveMember(Guid workspaceId, Guid userId, CancellationToken ct)
    {
        return await _workspaceService.RemoveMemberAsync(workspaceId, userId, CurrentUserId, ct);
    }

    [Authorize]
    [HttpPut("{workspaceId:guid}/members/{userId:guid}/role")]
    public async Task<Result> ChangeMemberRole(Guid workspaceId, Guid userId, [FromBody] ChangeMemberRoleRequest request, CancellationToken ct)
    {
        return await _workspaceService.ChangeMemberRoleAsync(workspaceId, userId, request.RoleName, CurrentUserId, ct);
    }
}

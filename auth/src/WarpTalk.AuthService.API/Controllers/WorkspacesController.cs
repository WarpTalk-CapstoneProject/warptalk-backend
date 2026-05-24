using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;

using WarpTalk.AuthService.Domain.Settings;

namespace WarpTalk.AuthService.API.Controllers;

[ApiController]
[Route("api/v1/workspaces")]
public class WorkspacesController : BaseApiController
{
    private readonly IWorkspaceService _workspaceService;

    public WorkspacesController(IWorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
    }

    [Authorize]
    [HttpPost]
    public async Task<Result<WorkspaceDto>> CreateWorkspace([FromBody] CreateWorkspaceRequest request, CancellationToken ct)
    {
        return await _workspaceService.CreateWorkspaceAsync(request, CurrentUserId, ct);
    }

    [Authorize]
    [HttpGet]
    public async Task<Result<PagedResult<WorkspaceDto>>> GetWorkspaces([FromQuery] GetWorkspacesQuery query, CancellationToken ct)
    {
        return await _workspaceService.GetWorkspacesAsync(query, CurrentUserId, ct);
    }

    [Authorize]
    [HttpGet("{id:guid}")]
    public async Task<Result<WorkspaceDto>> GetWorkspaceById(Guid id, CancellationToken ct)
    {
        return await _workspaceService.GetWorkspaceByIdAsync(id, CurrentUserId, ct);
    }

    [Authorize]
    [HttpPost("{id:guid}/select")]
    public async Task<Result<SelectWorkspaceResponse>> SelectWorkspace(Guid id, CancellationToken ct)
    {
        return await _workspaceService.SelectWorkspaceAsync(id, CurrentUserId, ct);
    }

    [Authorize]
    [HttpGet("{id:guid}/settings")]
    public async Task<Result<WorkspaceConfiguration>> GetWorkspaceSettings(Guid id, CancellationToken ct)
    {
        return await _workspaceService.GetWorkspaceSettingsAsync(id, CurrentUserId, ct);
    }

    [Authorize]
    [HttpPut("{id:guid}/settings")]
    public async Task<Result> UpdateWorkspaceSettings(Guid id, [FromBody] WorkspaceConfiguration settings, CancellationToken ct)
    {
        return await _workspaceService.UpdateWorkspaceSettingsAsync(id, settings, CurrentUserId, ct);
    }
}

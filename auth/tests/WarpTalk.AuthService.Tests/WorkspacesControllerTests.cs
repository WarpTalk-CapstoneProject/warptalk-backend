using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using WarpTalk.AuthService.API.Controllers;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests;

public class WorkspacesControllerTests
{
    private readonly IWorkspaceService _workspaceService;
    private readonly WorkspacesController _controller;
    private readonly Guid _userId;

    public WorkspacesControllerTests()
    {
        _workspaceService = Substitute.For<IWorkspaceService>();
        _controller = new WorkspacesController(_workspaceService);
        _userId = Guid.NewGuid();

        // Setup mock User Claims
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, _userId.ToString()) };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    [Fact]
    public async Task CreateWorkspace_ShouldReturn201Created_WhenSucceeds()
    {
        // Arrange
        var request = new CreateWorkspaceRequest("DeepMind Team", "AI Research", "https://cdn.com/logo.png");
        var expectedDto = new WorkspaceDto(Guid.NewGuid(), "DeepMind Team", "deepmind-team", "AI Research", "https://cdn.com/logo.png", "Owner", "business", DateTime.UtcNow);
        
        _workspaceService.CreateWorkspaceAsync(request, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(expectedDto));

        // Act
        var result = await _controller.CreateWorkspace(request, CancellationToken.None);

        // Assert
        var actionResult = Assert.IsType<Result<WorkspaceDto>>(result);
        Assert.True(actionResult.IsSuccess);
        Assert.Equal(expectedDto, actionResult.Value);
    }

    [Fact]
    public async Task CreateWorkspace_ShouldReturn400BadRequest_WhenValidationErrorOccurs()
    {
        // Arrange
        var request = new CreateWorkspaceRequest("", "AI Research", null);
        _workspaceService.CreateWorkspaceAsync(request, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<WorkspaceDto>("Workspace name is required.", ErrorCodes.ValidationError));

        // Act
        var result = await _controller.CreateWorkspace(request, CancellationToken.None);

        // Assert
        var actionResult = Assert.IsType<Result<WorkspaceDto>>(result);
        Assert.False(actionResult.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, actionResult.ErrorCode);
        Assert.Equal("Workspace name is required.", actionResult.Error);
    }

    [Fact]
    public async Task GetWorkspaces_ShouldReturn200Ok_WithPaginatedData()
    {
        // Arrange
        var query = new GetWorkspacesQuery(Page: 1, PageSize: 10, Search: null);
        var expectedList = new System.Collections.Generic.List<WorkspaceDto>
        {
            new(Guid.NewGuid(), "WS 1", "ws-1", null, null, "Member", "business", DateTime.UtcNow)
        };
        var expectedPagedResult = new PagedResult<WorkspaceDto>(expectedList, 1, 10, 1);

        _workspaceService.GetWorkspacesAsync(Arg.Any<GetWorkspacesQuery>(), _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(expectedPagedResult));

        // Act
        var result = await _controller.GetWorkspaces(query, CancellationToken.None);

        // Assert
        var actionResult = Assert.IsType<Result<PagedResult<WorkspaceDto>>>(result);
        Assert.True(actionResult.IsSuccess);
        Assert.Equal(expectedPagedResult, actionResult.Value);
    }

    [Fact]
    public async Task GetWorkspaceById_ShouldReturn200Ok_WhenFoundAndAuthorized()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var expectedDto = new WorkspaceDto(workspaceId, "DeepMind", "deepmind", null, null, "Owner", "business", DateTime.UtcNow);

        _workspaceService.GetWorkspaceByIdAsync(workspaceId, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(expectedDto));

        // Act
        var result = await _controller.GetWorkspaceById(workspaceId, CancellationToken.None);

        // Assert
        var actionResult = Assert.IsType<Result<WorkspaceDto>>(result);
        Assert.True(actionResult.IsSuccess);
        Assert.Equal(expectedDto, actionResult.Value);
    }

    [Fact]
    public async Task GetWorkspaceById_ShouldReturn403Forbidden_WhenUserNotMember()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        _workspaceService.GetWorkspaceByIdAsync(workspaceId, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<WorkspaceDto>("User is not a member.", ErrorCodes.Forbidden));

        // Act
        var result = await _controller.GetWorkspaceById(workspaceId, CancellationToken.None);

        // Assert
        var actionResult = Assert.IsType<Result<WorkspaceDto>>(result);
        Assert.False(actionResult.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, actionResult.ErrorCode);
    }

    [Fact]
    public async Task SelectWorkspace_ShouldReturn200Ok_WhenSelectionSuccessful()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var expectedResponse = new SelectWorkspaceResponse(workspaceId, "DeepMind", "deepmind");

        _workspaceService.SelectWorkspaceAsync(workspaceId, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(expectedResponse));

        // Act
        var result = await _controller.SelectWorkspace(workspaceId, CancellationToken.None);

        // Assert
        var actionResult = Assert.IsType<Result<SelectWorkspaceResponse>>(result);
        Assert.True(actionResult.IsSuccess);
        Assert.Equal(expectedResponse, actionResult.Value);
    }

    [Fact]
    public async Task SelectWorkspace_ShouldReturn403Forbidden_WhenNotMember()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        _workspaceService.SelectWorkspaceAsync(workspaceId, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<SelectWorkspaceResponse>("Not a member", ErrorCodes.Forbidden));

        // Act
        var result = await _controller.SelectWorkspace(workspaceId, CancellationToken.None);

        // Assert
        var actionResult = Assert.IsType<Result<SelectWorkspaceResponse>>(result);
        Assert.False(actionResult.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, actionResult.ErrorCode);
    }
}

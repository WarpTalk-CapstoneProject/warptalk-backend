using System;
using System.Collections.Generic;
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

public class WorkspaceMembersControllerTests
{
    private readonly IWorkspaceService _workspaceService;
    private readonly WorkspaceMembersController _controller;
    private readonly Guid _userId;

    public WorkspaceMembersControllerTests()
    {
        _workspaceService = Substitute.For<IWorkspaceService>();
        _controller = new WorkspaceMembersController(_workspaceService);
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
    public async Task ListMembers_ShouldReturnPaginatedList_WhenSucceeds()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var query = new GetWorkspacesQuery(Page: 1, PageSize: 10, Search: null);
        var expectedList = new List<WorkspaceMemberDto>
        {
            new(Guid.NewGuid(), workspaceId, _userId, "John Doe", "john@warptalk.vn", null, "Member", "Active", DateTime.UtcNow)
        };
        var expectedPagedResult = new PagedResult<WorkspaceMemberDto>(expectedList, 1, 10, 1);

        _workspaceService.ListMembersAsync(workspaceId, Arg.Any<GetWorkspacesQuery>(), _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(expectedPagedResult));

        // Act
        var result = await _controller.ListMembers(workspaceId, query, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = Assert.IsType<PagedResult<WorkspaceMemberDto>>(okResult.Value);
        Assert.Equal(expectedPagedResult, value);
    }

    [Fact]
    public async Task RemoveMember_ShouldReturnSuccess_WhenSucceeds()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();

        _workspaceService.RemoveMemberAsync(workspaceId, targetUserId, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _controller.RemoveMember(workspaceId, targetUserId, CancellationToken.None);

        // Assert
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task ChangeMemberRole_ShouldReturnSuccess_WhenSucceeds()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var request = new ChangeMemberRoleRequest("Admin");

        _workspaceService.ChangeMemberRoleAsync(workspaceId, targetUserId, "Admin", _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _controller.ChangeMemberRole(workspaceId, targetUserId, request, CancellationToken.None);

        // Assert
        Assert.IsType<NoContentResult>(result);
    }
}

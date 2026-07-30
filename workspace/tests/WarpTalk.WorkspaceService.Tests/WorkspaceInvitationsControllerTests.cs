using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.WorkspaceService.API.Controllers;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Enums;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceInvitationsControllerTests
{
    private readonly IWorkspaceInvitationService _service;
    private readonly WorkspaceInvitationsController _controller;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly string _email = "requester@example.com";

    public WorkspaceInvitationsControllerTests()
    {
        _service = Substitute.For<IWorkspaceInvitationService>();
        _controller = new WorkspaceInvitationsController(_service);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
            new Claim(ClaimTypes.Email, _email),
        };
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
            },
        };
    }

    [Fact]
    public async Task CreateJoinRequest_ShouldForwardSlugAndRequesterIdentity()
    {
        var response = CreateInvitationDto(InvitationStatus.REQUESTED.ToString());
        var command = new CreateJoinRequestCommand(null, "acme");
        _service.CreateJoinRequestAsync(command, _userId, _email, Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await _controller.CreateJoinRequest(command, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        await _service.Received(1).CreateJoinRequestAsync(command, _userId, _email, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMyJoinRequests_ShouldReturnUserScopedRecords()
    {
        var records = new List<WorkspaceInvitationDto>
        {
            CreateInvitationDto(InvitationStatus.REQUESTED.ToString()),
        };
        _service.GetJoinRequestsForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(records));

        var result = await _controller.GetMyJoinRequests(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(records, ok.Value);
    }

    [Fact]
    public async Task ListInvitations_ShouldForwardKindFilter()
    {
        var workspaceId = Guid.NewGuid();
        var query = new GetWorkspacesQuery(1, 100, null, "join-request");
        var response = new PagedResult<WorkspaceInvitationDto>(
            new List<WorkspaceInvitationDto>(),
            1,
            100,
            0);
        _service.ListInvitationsAsync(workspaceId, query, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await _controller.ListInvitations(workspaceId, query, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        await _service.Received(1).ListInvitationsAsync(workspaceId, query, _userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveJoinRequest_ShouldForwardSelectedMembershipType()
    {
        var workspaceId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var request = new ApproveJoinRequestRequest("Internal");
        var response = new ApproveJoinRequestResponse(
            CreateInvitationDto(InvitationStatus.ACCEPTED.ToString(), workspaceId, invitationId),
            "Sent");
        _service.ApproveJoinRequestAsync(workspaceId, invitationId, _userId, request, Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await _controller.ApproveJoinRequest(workspaceId, invitationId, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(response, ok.Value);
        await _service.Received(1).ApproveJoinRequestAsync(workspaceId, invitationId, _userId, request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveJoinRequest_ShouldMapForbiddenServiceResultTo403()
    {
        var workspaceId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        _service.ApproveJoinRequestAsync(workspaceId, invitationId, _userId, Arg.Any<ApproveJoinRequestRequest?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ApproveJoinRequestResponse>("Only Owner/Admin can approve.", ErrorCodes.Forbidden));

        var result = await _controller.ApproveJoinRequest(
            workspaceId,
            invitationId,
            new ApproveJoinRequestRequest("Admin"),
            CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task RejectJoinRequest_ShouldReturnNoContentAfterServiceSuccess()
    {
        var workspaceId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        _service.RejectJoinRequestAsync(workspaceId, invitationId, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.RejectJoinRequest(workspaceId, invitationId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    private WorkspaceInvitationDto CreateInvitationDto(
        string status,
        Guid? workspaceId = null,
        Guid? invitationId = null)
    {
        return new WorkspaceInvitationDto(
            invitationId ?? Guid.NewGuid(),
            workspaceId ?? Guid.NewGuid(),
            _email,
            "Member",
            status,
            "External",
            "NotSent",
            null,
            null,
            0,
            DateTime.UtcNow.AddDays(7),
            DateTime.UtcNow,
            null,
            _userId,
            null,
            null);
    }
}

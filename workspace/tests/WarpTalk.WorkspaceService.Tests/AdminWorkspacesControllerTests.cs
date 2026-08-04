using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.WorkspaceService.API.Controllers;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class AdminWorkspacesControllerTests
{
    private readonly IAdminWorkspaceService _adminWorkspaceService = Substitute.For<IAdminWorkspaceService>();
    private readonly AdminWorkspacesController _controller;
    private readonly Guid _actorId = Guid.NewGuid();

    public AdminWorkspacesControllerTests()
    {
        _controller = new AdminWorkspacesController(_adminWorkspaceService);
        SetUser(new Claim(ClaimTypes.NameIdentifier, _actorId.ToString()));
    }

    private void SetUser(params Claim[] claims)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal },
        };
    }

    private static AdminWorkspaceDetailDto Detail(Guid id, string status) => new(
        id,
        "Acme Localization",
        "acme-localization",
        null,
        status,
        new AdminWorkspaceOwnerDto(Guid.NewGuid(), "Mai Tran", "mai@acme.com", null, true),
        MemberCount: 3,
        InternalMemberCount: 2,
        ExternalMemberCount: 1,
        PendingInvitationCount: 0,
        DocumentCount: 0,
        VerifiedDomainCount: 0,
        AllowExternalCollaboration: true,
        RequireVerifiedDomainForInternal: false,
        CreatedAt: DateTime.UtcNow.AddDays(-10),
        UpdatedAt: DateTime.UtcNow,
        LastActivityAt: DateTime.UtcNow,
        DeletedAt: null,
        CurrentSuspension: null,
        LifecycleHistory: Array.Empty<AdminWorkspaceLifecycleEventDto>());

    [Fact]
    public void Controller_IsGatedOnTheSharedSystemAdminPolicy()
    {
        var authorize = typeof(AdminWorkspacesController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .SingleOrDefault();

        Assert.NotNull(authorize);
        Assert.Equal(SystemAdminAuthorization.PolicyName, authorize!.Policy);
        // The shared policy is the single gate — no per-controller role string alongside it.
        Assert.Null(authorize.Roles);
    }

    [Fact]
    public void Controller_IsRoutedUnderTheAdminNamespace()
    {
        var route = typeof(AdminWorkspacesController).GetCustomAttribute<RouteAttribute>();

        Assert.NotNull(route);
        Assert.Equal("api/v1/admin/workspaces", route!.Template);
    }

    [Fact]
    public async Task GetDirectory_Returns200WithPagedPayload()
    {
        var expected = new AdminPagedResult<AdminWorkspaceSummaryDto>(
            new List<AdminWorkspaceSummaryDto>(), 1, 20, 0);
        _adminWorkspaceService.GetDirectoryAsync(Arg.Any<AdminWorkspaceDirectoryQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(expected));

        var result = await _controller.GetDirectory(new AdminWorkspaceDirectoryQuery(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(expected, ok.Value);
    }

    [Fact]
    public async Task GetDirectory_Returns400ForRejectedFilters()
    {
        _adminWorkspaceService.GetDirectoryAsync(Arg.Any<AdminWorkspaceDirectoryQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AdminPagedResult<AdminWorkspaceSummaryDto>>(
                WorkspaceAdminErrors.UnknownStatusFilter, ErrorCodes.ValidationError));

        var result = await _controller.GetDirectory(
            new AdminWorkspaceDirectoryQuery { Status = "trial" }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ApiErrorResponse>(badRequest.Value);
        Assert.Equal(ErrorCodes.ValidationError, error.Code);
    }

    [Fact]
    public async Task GetDetail_Returns404WhenMissing()
    {
        _adminWorkspaceService.GetDetailAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AdminWorkspaceDetailDto>(
                WorkspaceConstants.Errors.WorkspaceNotFound, ErrorCodes.NotFound));

        var result = await _controller.GetDetail(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Suspend_PassesTheAuthenticatedActorNotTheRequestBody()
    {
        var id = Guid.NewGuid();
        _adminWorkspaceService
            .SuspendAsync(id, "Abuse report", _actorId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Detail(id, WorkspaceLifecycleStatus.Suspended)));

        var result = await _controller.Suspend(
            id, new AdminWorkspaceLifecycleRequest("Abuse report"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var detail = Assert.IsType<AdminWorkspaceDetailDto>(ok.Value);
        Assert.Equal(WorkspaceLifecycleStatus.Suspended, detail.Status);
        await _adminWorkspaceService.Received(1)
            .SuspendAsync(id, "Abuse report", _actorId, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Suspend_Returns409OnAnInvalidTransition()
    {
        _adminWorkspaceService
            .SuspendAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AdminWorkspaceDetailDto>(
                WorkspaceAdminErrors.AlreadySuspended, ErrorCodes.Conflict));

        var result = await _controller.Suspend(
            Guid.NewGuid(), new AdminWorkspaceLifecycleRequest("Abuse report"), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var error = Assert.IsType<ApiErrorResponse>(conflict.Value);
        Assert.Equal(ErrorCodes.Conflict, error.Code);
    }

    [Fact]
    public async Task Suspend_Returns401WhenTheTokenCarriesNoUserId()
    {
        SetUser();

        var result = await _controller.Suspend(
            Guid.NewGuid(), new AdminWorkspaceLifecycleRequest("Abuse report"), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        await _adminWorkspaceService.DidNotReceive().SuspendAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reactivate_Returns200AndForwardsTheReason()
    {
        var id = Guid.NewGuid();
        _adminWorkspaceService
            .ReactivateAsync(id, "Invoice settled", _actorId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Detail(id, WorkspaceLifecycleStatus.Active)));

        var result = await _controller.Reactivate(
            id, new AdminWorkspaceLifecycleRequest("Invoice settled"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(WorkspaceLifecycleStatus.Active, Assert.IsType<AdminWorkspaceDetailDto>(ok.Value).Status);
    }
}

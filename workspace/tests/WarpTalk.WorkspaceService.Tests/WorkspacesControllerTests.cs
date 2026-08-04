using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using WarpTalk.WorkspaceService.API.Controllers;
using WarpTalk.WorkspaceService.Application.DTOs.Workspace;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Settings;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

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
        var request = new CreateWorkspaceRequest("DeepMind Team", "https://cdn.com/logo.png");
        var expectedDto = new WorkspaceDto(Guid.NewGuid(), "DeepMind Team", "deepmind-team", "https://cdn.com/logo.png", "Owner", DateTime.UtcNow, "en");

        _workspaceService.CreateWorkspaceAsync(request, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(expectedDto));

        // Act
        var result = await _controller.CreateWorkspace(request, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = Assert.IsType<WorkspaceDto>(okResult.Value);
        Assert.Equal(expectedDto, value);
    }

    [Fact]
    public async Task CreateWorkspace_ShouldReturn400BadRequest_WhenValidationErrorOccurs()
    {
        // Arrange
        var request = new CreateWorkspaceRequest("", null);
        _workspaceService.CreateWorkspaceAsync(request, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<WorkspaceDto>("Workspace name is required.", ErrorCodes.ValidationError));

        // Act
        var result = await _controller.CreateWorkspace(request, CancellationToken.None);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        var value = Assert.IsType<ApiErrorResponse>(badRequestResult.Value);
        Assert.Equal(ErrorCodes.ValidationError, value.Code);
        Assert.Equal("Workspace name is required.", value.Error);
    }

    [Fact]
    public async Task GetWorkspaces_ShouldReturn200Ok_WithPaginatedData()
    {
        // Arrange
        var query = new GetWorkspacesQuery(Page: 1, PageSize: 10, Search: null);
        var expectedList = new System.Collections.Generic.List<WorkspaceDto>
        {
            new(Guid.NewGuid(), "WS 1", "ws-1", null, "Member", DateTime.UtcNow, "en")
        };
        var expectedPagedResult = new PagedResult<WorkspaceDto>(expectedList, 1, 10, 1);

        _workspaceService.GetWorkspacesAsync(Arg.Any<GetWorkspacesQuery>(), _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(expectedPagedResult));

        // Act
        var result = await _controller.GetWorkspaces(query, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = Assert.IsType<PagedResult<WorkspaceDto>>(okResult.Value);
        Assert.Equal(expectedPagedResult, value);
    }

    [Fact]
    public async Task GetWorkspaceById_ShouldReturn200Ok_WhenFoundAndAuthorized()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var expectedDto = new WorkspaceDto(workspaceId, "DeepMind", "deepmind", null, "Owner", DateTime.UtcNow, "en");

        _workspaceService.GetWorkspaceByIdAsync(workspaceId, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(expectedDto));

        // Act
        var result = await _controller.GetWorkspaceById(workspaceId, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = Assert.IsType<WorkspaceDto>(okResult.Value);
        Assert.Equal(expectedDto, value);
    }

    [Fact]
    public async Task GetWorkspaceById_ShouldUseAdminLookup_WhenUserHasPlatformAdminRole()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var expectedDto = new WorkspaceDto(workspaceId, "FitPick", "fitpick", null, "admin", DateTime.UtcNow, "en");

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
            new Claim(ClaimTypes.Role, "admin")
        };
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")) }
        };

        _workspaceService.GetWorkspaceByIdForAdminAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(expectedDto));

        // Act
        var result = await _controller.GetWorkspaceById(workspaceId, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = Assert.IsType<WorkspaceDto>(okResult.Value);
        Assert.Equal(expectedDto, value);
        await _workspaceService.DidNotReceive().GetWorkspaceByIdAsync(workspaceId, _userId, Arg.Any<CancellationToken>());
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
        var forbiddenResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbiddenResult.StatusCode);
        var value = Assert.IsType<ApiErrorResponse>(forbiddenResult.Value);
        Assert.Equal(ErrorCodes.Forbidden, value.Code);
    }

    [Fact]
    public async Task SelectWorkspace_ShouldReturn200Ok_WhenSelectionSuccessful()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var expectedResponse = new SelectWorkspaceResponse(workspaceId, "DeepMind", "deepmind", "en");

        _workspaceService.SelectWorkspaceAsync(workspaceId, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(expectedResponse));

        // Act
        var result = await _controller.SelectWorkspace(workspaceId, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = Assert.IsType<SelectWorkspaceResponse>(okResult.Value);
        Assert.Equal(expectedResponse, value);
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
        var forbiddenResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbiddenResult.StatusCode);
        var value = Assert.IsType<ApiErrorResponse>(forbiddenResult.Value);
        Assert.Equal(ErrorCodes.Forbidden, value.Code);
    }

    [Fact]
    public async Task GetWorkspaceSettings_ShouldReturnSettings_WhenSuccessful()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var expectedSettings = new WorkspaceSettingsDto(
            "vi",
            "UTC",
            new List<string>(),
            true,
            5,
            30,
            true,
            new List<string> { "company.com" },
            true,
            true,
            null,
            false
        );
        _workspaceService.GetWorkspaceSettingsAsync(workspaceId, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(expectedSettings));

        // Act
        var result = await _controller.GetWorkspaceSettings(workspaceId, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = Assert.IsType<WorkspaceSettingsDto>(okResult.Value);
        Assert.Equal(expectedSettings, value);
    }

    [Fact]
    public async Task UpdateWorkspaceSettings_ShouldReturnSuccess_WhenSuccessful()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var newSettings = new WorkspaceSettingsDto(
            "vi",
            "UTC",
            new List<string>(),
            true,
            5,
            30,
            true,
            new List<string> { "company.com" },
            true,
            true,
            null,
            false
        );
        _workspaceService.UpdateWorkspaceSettingsAsync(workspaceId, newSettings, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        // Act
        var result = await _controller.UpdateWorkspaceSettings(workspaceId, newSettings, CancellationToken.None);

        // Assert
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task UpdateWorkspaceSettings_ShouldReturnForbidden_WhenUnauthorized()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var newSettings = new WorkspaceSettingsDto(
            "vi",
            "UTC",
            new List<string>(),
            true,
            5,
            30,
            true,
            new List<string> { "company.com" },
            true,
            true,
            null,
            false
        );
        _workspaceService.UpdateWorkspaceSettingsAsync(workspaceId, newSettings, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Forbidden", ErrorCodes.Forbidden));

        // Act
        var result = await _controller.UpdateWorkspaceSettings(workspaceId, newSettings, CancellationToken.None);

        // Assert
        var forbiddenResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbiddenResult.StatusCode);
        var value = Assert.IsType<ApiErrorResponse>(forbiddenResult.Value);
        Assert.Equal(ErrorCodes.Forbidden, value.Code);
    }

    [Fact]
    public async Task PatchWorkspaceSettings_ShouldPreserveVerifiedDomains_WhenPatchOmitsThem()
    {
        var workspaceId = Guid.NewGuid();
        var current = new WorkspaceSettingsDto(
            "en",
            "UTC",
            new List<string>(),
            true,
            5,
            30,
            true,
            new List<string> { "company.com" },
            true,
            true,
            null,
            false);
        WorkspaceSettingsDto? savedSettings = null;

        _workspaceService.GetWorkspaceSettingsAsync(workspaceId, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(current));
        _workspaceService.UpdateWorkspaceSettingsAsync(
                workspaceId,
                Arg.Do<WorkspaceSettingsDto>(settings => savedSettings = settings),
                _userId,
                Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.PatchWorkspaceSettings(
            workspaceId,
            new WorkspaceSettingsPatchRequest(ArtifactRetentionDays: 60),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var merged = Assert.IsType<WorkspaceSettingsDto>(ok.Value);
        Assert.Equal(new List<string> { "company.com" }, merged.VerifiedDomains);
        Assert.Equal(new List<string> { "company.com" }, savedSettings?.VerifiedDomains);
    }

    [Fact]
    public async Task PatchWorkspaceSettings_ShouldSendExplicitEmptyVerifiedDomains_WhenStrictModeIsOff()
    {
        var workspaceId = Guid.NewGuid();
        var current = new WorkspaceSettingsDto(
            "en",
            "UTC",
            new List<string>(),
            true,
            5,
            30,
            true,
            new List<string> { "company.com" },
            true,
            false,
            null,
            false);
        WorkspaceSettingsDto? savedSettings = null;

        _workspaceService.GetWorkspaceSettingsAsync(workspaceId, _userId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(current));
        _workspaceService.UpdateWorkspaceSettingsAsync(
                workspaceId,
                Arg.Do<WorkspaceSettingsDto>(settings => savedSettings = settings),
                _userId,
                Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.PatchWorkspaceSettings(
            workspaceId,
            new WorkspaceSettingsPatchRequest(VerifiedDomains: new List<string>()),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var merged = Assert.IsType<WorkspaceSettingsDto>(ok.Value);
        Assert.Empty(merged.VerifiedDomains);
        Assert.Empty(savedSettings?.VerifiedDomains ?? new List<string> { "unexpected" });
    }

}

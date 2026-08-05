using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Application.Services;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// These cases moved here from WorkspaceGrpcServiceTests when the membership and
/// workspace-policy rules moved out of the gRPC boundary (WT-239). They assert the
/// same behaviour against the layer that now owns it.
/// </summary>
public class WorkspaceDirectoryServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly ITranslationRoomClient _translationRoomClient;
    private readonly WorkspaceDirectoryService _service;

    public WorkspaceDirectoryServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _authIdentity = Substitute.For<IAuthIdentityClient>();
        _translationRoomClient = Substitute.For<ITranslationRoomClient>();
        _service = new WorkspaceDirectoryService(_unitOfWork, _authIdentity, _translationRoomClient);
    }

    private void StubMember(WorkspaceMember? member) =>
        _unitOfWork.WorkspaceMemberRepository
            .FirstOrDefaultAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(member!);

    private void StubWorkspace(Guid workspaceId, Workspace? workspace) =>
        _unitOfWork.WorkspaceRepository
            .GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(workspace!);

    [Fact]
    public async Task GetMemberDetailsAsync_ReturnsDetails_WhenMemberExists()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        StubMember(new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = roleId,
            MembershipType = "internal",
            Status = "Active",
            CanCreateMeetings = true
        });
        _authIdentity.GetRoleByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = roleId, Name = "Admin" });

        var result = await _service.GetMemberDetailsAsync(workspaceId, userId);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("Admin", result.Value!.RoleName);
        Assert.Equal("internal", result.Value.MembershipType);
        Assert.True(result.Value.IsActive);
        Assert.True(result.Value.CanCreateMeetings);
    }

    [Fact]
    public async Task GetMemberDetailsAsync_SucceedsWithNull_WhenNotAMember()
    {
        StubMember(null);

        var result = await _service.GetMemberDetailsAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task GetWorkspaceNamesAsync_ReturnsOnlyExistingWorkspaces()
    {
        var firstId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        _unitOfWork.WorkspaceRepository
            .FindAsync(
                Arg.Any<Expression<Func<Workspace, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new[] { new Workspace { Id = firstId, Name = "WarpTalk Team" } });

        var result = await _service.GetWorkspaceNamesAsync(new[] { firstId, missingId });

        Assert.True(result.IsSuccess);
        var only = Assert.Single(result.Value!);
        Assert.Equal(firstId, only.WorkspaceId);
        Assert.Equal("WarpTalk Team", only.WorkspaceName);
    }

    [Fact]
    public async Task GetWorkspaceNamesAsync_ReturnsEmpty_WithoutQuerying_WhenNoIds()
    {
        var result = await _service.GetWorkspaceNamesAsync(Array.Empty<Guid>());

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
        await _unitOfWork.WorkspaceRepository.DidNotReceiveWithAnyArgs()
            .FindAsync(default!, default!, default);
    }

    [Fact]
    public async Task ValidateMeetingCreationAsync_Allows_WhenMemberHasPermissionAndActive()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        StubMember(new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            Status = "Active",
            CanCreateMeetings = true
        });
        StubWorkspace(workspaceId, new Workspace
        {
            Id = workspaceId,
            Settings = "{\"AllowedTargetLanguages\":[\"en\",\"vi\"],\"MaxActiveRooms\":10}"
        });

        var result = await _service.ValidateMeetingCreationAsync(workspaceId, userId, new[] { "vi" });

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsAllowed);
        Assert.Empty(result.Value.ErrorMessage);
    }

    [Fact]
    public async Task ValidateMeetingCreationAsync_Denies_WhenNotAMember()
    {
        StubMember(null);

        var result = await _service.ValidateMeetingCreationAsync(
            Guid.NewGuid(), Guid.NewGuid(), Array.Empty<string>());

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsAllowed);
        Assert.Contains("not a member", result.Value.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateMeetingCreationAsync_Denies_WhenMemberCannotCreateMeetings()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        StubMember(new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            Status = "Active",
            CanCreateMeetings = false
        });

        var result = await _service.ValidateMeetingCreationAsync(workspaceId, userId, Array.Empty<string>());

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsAllowed);
        Assert.Contains("permission", result.Value.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateMeetingCreationAsync_Denies_WhenTargetLanguageNotAllowed()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        StubMember(new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            Status = "Active",
            CanCreateMeetings = true
        });
        StubWorkspace(workspaceId, new Workspace
        {
            Id = workspaceId,
            Settings = "{\"AllowedTargetLanguages\":[\"vi\"],\"MaxActiveRooms\":10}"
        });

        var result = await _service.ValidateMeetingCreationAsync(workspaceId, userId, new[] { "en" });

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsAllowed);
        Assert.Contains("not allowed", result.Value.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateMeetingCreationAsync_Denies_WhenActiveRoomLimitReached()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        StubMember(new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            Status = "Active",
            CanCreateMeetings = true
        });
        StubWorkspace(workspaceId, new Workspace { Id = workspaceId, Settings = "{\"MaxActiveRooms\":2}" });
        _translationRoomClient
            .GetActiveRoomCountAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await _service.ValidateMeetingCreationAsync(workspaceId, userId, Array.Empty<string>());

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsAllowed);
        Assert.Contains("active room limit", result.Value.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSettingsAsync_ReturnsSettings_WhenWorkspaceExists()
    {
        var workspaceId = Guid.NewGuid();
        StubWorkspace(workspaceId, new Workspace
        {
            Id = workspaceId,
            AllowExternalCollaboration = true,
            Settings = "{\"ArtifactRetentionDays\":15,\"AllowExternalCollaboration\":true}"
        });

        var result = await _service.GetSettingsAsync(workspaceId);

        Assert.True(result.IsSuccess);
        Assert.Equal(15, result.Value!.ArtifactRetentionDays);
        Assert.True(result.Value.AllowExternalCollaboration);
    }

    [Fact]
    public async Task GetSettingsAsync_Fails_WhenWorkspaceDoesNotExist()
    {
        var workspaceId = Guid.NewGuid();
        StubWorkspace(workspaceId, null);

        var result = await _service.GetSettingsAsync(workspaceId);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task GetSettingsAsync_DefaultsAllowExternalLlmToTrue_WhenAiUsagePolicyNotConfigured()
    {
        // Opt-out semantics: no AiUsagePolicy at all ⇒ allowed.
        var workspaceId = Guid.NewGuid();
        StubWorkspace(workspaceId, new Workspace
        {
            Id = workspaceId,
            Settings = "{\"ArtifactRetentionDays\":15}"
        });

        var result = await _service.GetSettingsAsync(workspaceId);

        Assert.True(result.Value!.AllowExternalLlm);
    }

    [Fact]
    public async Task GetSettingsAsync_NormalizesAllowExternalLlmToTrue_WhenPayloadSetsFalse()
    {
        var workspaceId = Guid.NewGuid();
        StubWorkspace(workspaceId, new Workspace
        {
            Id = workspaceId,
            Settings = "{\"AiUsagePolicy\":{\"AllowExternalLlm\":false}}"
        });

        var result = await _service.GetSettingsAsync(workspaceId);

        Assert.True(result.Value!.AllowExternalLlm);
    }

    [Fact]
    public async Task GetSettingsAsync_DefaultsUseGlobalGlossaryToTrue_WhenAiUsagePolicyNotConfigured()
    {
        var workspaceId = Guid.NewGuid();
        StubWorkspace(workspaceId, new Workspace
        {
            Id = workspaceId,
            Settings = "{\"ArtifactRetentionDays\":15}"
        });

        var result = await _service.GetSettingsAsync(workspaceId);

        Assert.True(result.Value!.UseGlobalGlossary);
    }

    [Fact]
    public async Task GetSettingsAsync_ReturnsUseGlobalGlossaryFalse_WhenWorkspaceOptedOut()
    {
        var workspaceId = Guid.NewGuid();
        StubWorkspace(workspaceId, new Workspace
        {
            Id = workspaceId,
            Settings = "{\"AiUsagePolicy\":{\"UseGlobalGlossary\":false}}"
        });

        var result = await _service.GetSettingsAsync(workspaceId);

        Assert.False(result.Value!.UseGlobalGlossary);
    }

    [Fact]
    public async Task GetPreflightAsync_Fails_WhenWorkspaceDoesNotExist()
    {
        var workspaceId = Guid.NewGuid();
        StubWorkspace(workspaceId, null);

        var result = await _service.GetPreflightAsync(workspaceId, "someone@example.com");

        Assert.False(result.IsSuccess);
    }
}

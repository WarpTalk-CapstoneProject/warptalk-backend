using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using NSubstitute;
using WarpTalk.Shared.Protos;
using WarpTalk.WorkspaceService.API.GrpcServices;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class WorkspaceGrpcServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly ITranslationRoomClient _translationRoomClient;
    private readonly WorkspaceGrpcService _service;
    private readonly ServerCallContext _context;

    public WorkspaceGrpcServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _authIdentity = Substitute.For<IAuthIdentityClient>();
        _translationRoomClient = Substitute.For<ITranslationRoomClient>();

        // WT-263: no IBillingSubscriptionClient here at all. The service no longer takes one,
        // because meeting creation makes no call to BillingService — that is the ticket.
        _service = new WorkspaceGrpcService(_unitOfWork, _authIdentity, _translationRoomClient);
        _context = new TestServerCallContext(CancellationToken.None);
    }

    /// <summary>Builds the stored snapshot JSON in the shape the entitlements.changed consumer writes.</summary>
    private static string SnapshotJson(params (string Key, string Value, string Source)[] entries) =>
        "{" + string.Join(",", entries.Select(entry =>
            $"\"{entry.Key}\":{{\"value\":\"{entry.Value}\",\"source\":\"{entry.Source}\"}}")) + "}";

    /// <summary>Arranges an active member of a workspace whose own settings permit everything, so a
    /// quota test only exercises the entitlement snapshot.</summary>
    private Guid ArrangePermittedMember(Guid workspaceId, string settings = "{\"MaxActiveRooms\":10}")
    {
        var userId = Guid.NewGuid();

        _unitOfWork.WorkspaceMemberRepository
            .FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceMember
            {
                WorkspaceId = workspaceId,
                UserId = userId,
                Status = "Active",
                CanCreateMeetings = true
            });

        _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new Workspace { Id = workspaceId, Settings = settings });

        return userId;
    }

    /// <summary>Gives the workspace a local entitlement snapshot, as the consumer would have.</summary>
    private void ArrangeSnapshot(
        Guid workspaceId,
        string entitlementsJson,
        bool hasActiveSubscription = true)
    {
        _unitOfWork.WorkspaceEntitlementSnapshotRepository
            .GetForWorkspaceAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceEntitlementSnapshot
            {
                WorkspaceId = workspaceId,
                EntitlementsJson = entitlementsJson,
                HasActiveSubscription = hasActiveSubscription,
                ResolvedAt = DateTime.UtcNow
            });
    }

    // ── WT-263: max_languages enforced from the local snapshot ────────────────

    [Fact]
    public async Task ValidateMeetingCreation_ShouldDeny_WhenTargetLanguagesExceedPlanQuota()
    {
        var workspaceId = Guid.NewGuid();
        var userId = ArrangePermittedMember(workspaceId);
        ArrangeSnapshot(workspaceId, SnapshotJson(("max_languages", "2", "plan:startup")));

        var request = new ValidateMeetingCreationRequest
        {
            WorkspaceId = workspaceId.ToString(),
            UserId = userId.ToString(),
            TargetLanguages = { "vi", "en", "ja" }
        };

        var response = await _service.ValidateMeetingCreation(request, _context);

        Assert.False(response.IsAllowed);
        Assert.Contains("2", response.ErrorMessage);
    }

    [Fact]
    public async Task ValidateMeetingCreation_ShouldAllow_WhenTargetLanguagesFitThePlanQuota()
    {
        var workspaceId = Guid.NewGuid();
        var userId = ArrangePermittedMember(workspaceId);
        ArrangeSnapshot(workspaceId, SnapshotJson(("max_languages", "3", "plan:enterprise")));

        var request = new ValidateMeetingCreationRequest
        {
            WorkspaceId = workspaceId.ToString(),
            UserId = userId.ToString(),
            TargetLanguages = { "vi", "en", "ja" }
        };

        var response = await _service.ValidateMeetingCreation(request, _context);

        Assert.True(response.IsAllowed);
    }

    /// <summary>
    /// THE HEADLINE PROOF that the architecture works, and the exact inversion of the WT-262 test it
    /// replaces (ValidateMeetingCreation_ShouldDeny_WhenBillingIsUnreachable).
    ///
    /// There is no billing client to make unreachable any more: the service does not take one, so
    /// this test cannot even express "billing is down" — the dependency is gone. What it asserts is
    /// the consequence: a multi-language meeting is created from the local snapshot alone, with no
    /// remote call in the path, so BillingService's availability cannot deny it.
    /// </summary>
    [Fact]
    public async Task ValidateMeetingCreation_ShouldAllow_WithBillingUnreachable_BecauseItReadsTheLocalSnapshot()
    {
        var workspaceId = Guid.NewGuid();
        var userId = ArrangePermittedMember(workspaceId);
        ArrangeSnapshot(workspaceId, SnapshotJson(("max_languages", "3", "plan:enterprise")));

        var request = new ValidateMeetingCreationRequest
        {
            WorkspaceId = workspaceId.ToString(),
            UserId = userId.ToString(),
            TargetLanguages = { "vi", "en", "ja" }
        };

        var response = await _service.ValidateMeetingCreation(request, _context);

        Assert.True(response.IsAllowed);
        Assert.Empty(response.ErrorMessage);

        // The only collaborators reached are local state and the room count. No billing client
        // exists on this type — asserting that is what pins the stopgap as removed.
        Assert.DoesNotContain(
            typeof(WorkspaceGrpcService).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType == typeof(IBillingSubscriptionClient));
    }

    /// <summary>
    /// COLD START: a workspace with no snapshot yet. Plan quotas are not in force, so creation
    /// succeeds — the same answer a workspace with no live subscription already got, and the reason
    /// a brand-new workspace is not locked out while its first event is in flight.
    /// </summary>
    [Fact]
    public async Task ValidateMeetingCreation_ShouldAllow_OnColdStart_WhenNoSnapshotExistsYet()
    {
        var workspaceId = Guid.NewGuid();
        var userId = ArrangePermittedMember(workspaceId);

        _unitOfWork.WorkspaceEntitlementSnapshotRepository
            .GetForWorkspaceAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns((WorkspaceEntitlementSnapshot?)null);

        var request = new ValidateMeetingCreationRequest
        {
            WorkspaceId = workspaceId.ToString(),
            UserId = userId.ToString(),
            TargetLanguages = { "vi", "en", "ja", "ko" }
        };

        var response = await _service.ValidateMeetingCreation(request, _context);

        Assert.True(response.IsAllowed);
    }

    /// <summary>
    /// A workspace with no live plan has no max_languages in force. The workspace's own
    /// AllowedTargetLanguages policy still governs it — this must not become "no subscription, no
    /// meetings".
    /// </summary>
    [Fact]
    public async Task ValidateMeetingCreation_ShouldAllow_WhenWorkspaceHasNoActiveSubscription()
    {
        var workspaceId = Guid.NewGuid();
        var userId = ArrangePermittedMember(workspaceId);
        ArrangeSnapshot(
            workspaceId,
            SnapshotJson(("max_languages", "1", "platform_default")),
            hasActiveSubscription: false);

        var request = new ValidateMeetingCreationRequest
        {
            WorkspaceId = workspaceId.ToString(),
            UserId = userId.ToString(),
            TargetLanguages = { "vi", "en", "ja" }
        };

        var response = await _service.ValidateMeetingCreation(request, _context);

        Assert.True(response.IsAllowed);
    }

    /// <summary>
    /// WT-263: max_active_rooms is an ordinary entitlement key now. A resolved workspace_override
    /// beats the settings-JSON copy, with no sentinel value anywhere.
    /// </summary>
    [Fact]
    public async Task ValidateMeetingCreation_ShouldEnforceMaxActiveRooms_FromTheSnapshot()
    {
        var workspaceId = Guid.NewGuid();
        // Settings JSON says 10; the resolved entitlement says the owner tightened to 2.
        var userId = ArrangePermittedMember(workspaceId, "{\"MaxActiveRooms\":10}");
        ArrangeSnapshot(workspaceId, SnapshotJson(("max_active_rooms", "2", "workspace_override")));

        _translationRoomClient
            .GetActiveRoomCountAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(2);

        var response = await _service.ValidateMeetingCreation(
            new ValidateMeetingCreationRequest
            {
                WorkspaceId = workspaceId.ToString(),
                UserId = userId.ToString()
            },
            _context);

        Assert.False(response.IsAllowed);
        Assert.Contains("active room limit (2)", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetWorkspaceMemberDetails_ShouldReturnIsMemberTrue_WhenMemberExists()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var member = new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            RoleId = roleId,
            MembershipType = "internal",
            Status = "Active",
            CanCreateMeetings = true
        };

        _unitOfWork.WorkspaceMemberRepository
            .FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        _authIdentity.GetRoleByIdAsync(roleId, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = roleId, Name = "Admin" });

        var request = new GetWorkspaceMemberRequest
        {
            WorkspaceId = workspaceId.ToString(),
            UserId = userId.ToString()
        };

        // Act
        var response = await _service.GetWorkspaceMemberDetails(request, _context);

        // Assert
        Assert.True(response.IsMember);
        Assert.Equal("Admin", response.RoleName);
        Assert.Equal("internal", response.MembershipType);
        Assert.True(response.IsActive);
        Assert.True(response.CanCreateMeetings);
    }

    [Fact]
    public async Task GetWorkspaceMemberDetails_ShouldReturnIsMemberFalse_WhenMemberDoesNotExist()
    {
        // Arrange
        _unitOfWork.WorkspaceMemberRepository
            .FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((WorkspaceMember)null!);

        var request = new GetWorkspaceMemberRequest
        {
            WorkspaceId = Guid.NewGuid().ToString(),
            UserId = Guid.NewGuid().ToString()
        };

        // Act
        var response = await _service.GetWorkspaceMemberDetails(request, _context);

        // Assert
        Assert.False(response.IsMember);
    }

    [Fact]
    public async Task GetWorkspaceNames_ReturnsOnlyExistingWorkspaces()
    {
        var firstId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        _unitOfWork.WorkspaceRepository
            .FindAsync(Arg.Any<Expression<Func<Workspace, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new Workspace { Id = firstId, Name = "WarpTalk Team" }
            });
        var request = new GetWorkspaceNamesRequest();
        request.WorkspaceIds.Add(firstId.ToString());
        request.WorkspaceIds.Add(missingId.ToString());

        var response = await _service.GetWorkspaceNames(request, _context);

        Assert.Single(response.Workspaces);
        Assert.Equal(firstId.ToString(), response.Workspaces[0].WorkspaceId);
        Assert.Equal("WarpTalk Team", response.Workspaces[0].WorkspaceName);
    }

    [Fact]
    public async Task ValidateMeetingCreation_ShouldAllow_WhenMemberHasPermissionAndActive()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var member = new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            Status = "Active",
            CanCreateMeetings = true
        };

        var workspace = new Workspace
        {
            Id = workspaceId,
            Settings = "{\"AllowedTargetLanguages\":[\"en\",\"vi\"],\"MaxActiveRooms\":10}"
        };

        _unitOfWork.WorkspaceMemberRepository
            .FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(workspace);

        var request = new ValidateMeetingCreationRequest
        {
            WorkspaceId = workspaceId.ToString(),
            UserId = userId.ToString(),
            TargetLanguages = { "vi" }
        };

        // Act
        var response = await _service.ValidateMeetingCreation(request, _context);

        // Assert
        Assert.True(response.IsAllowed);
        Assert.Empty(response.ErrorMessage);
    }

    [Fact]
    public async Task ValidateMeetingCreation_ShouldDeny_WhenMemberCannotCreateMeetings()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var member = new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            Status = "Active",
            CanCreateMeetings = false
        };

        _unitOfWork.WorkspaceMemberRepository
            .FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        var request = new ValidateMeetingCreationRequest
        {
            WorkspaceId = workspaceId.ToString(),
            UserId = userId.ToString()
        };

        // Act
        var response = await _service.ValidateMeetingCreation(request, _context);

        // Assert
        Assert.False(response.IsAllowed);
        Assert.Contains("permission", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateMeetingCreation_ShouldDeny_WhenTargetLanguageNotAllowedByWorkspace()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var member = new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            Status = "Active",
            CanCreateMeetings = true
        };

        var workspace = new Workspace
        {
            Id = workspaceId,
            Settings = "{\"AllowedTargetLanguages\":[\"vi\"],\"MaxActiveRooms\":10}"
        };

        _unitOfWork.WorkspaceMemberRepository
            .FirstOrDefaultAsync(Arg.Any<Expression<Func<WorkspaceMember, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(member);

        _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(workspace);

        var request = new ValidateMeetingCreationRequest
        {
            WorkspaceId = workspaceId.ToString(),
            UserId = userId.ToString(),
            TargetLanguages = { "en" } // Not allowed in workspace settings
        };

        // Act
        var response = await _service.ValidateMeetingCreation(request, _context);

        // Assert
        Assert.False(response.IsAllowed);
        Assert.Contains("not allowed", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateMeetingCreation_ShouldDeny_WhenWorkspaceReachedActiveRoomLimit()
    {
        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var member = new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            Status = "Active",
            CanCreateMeetings = true
        };
        var workspace = new Workspace
        {
            Id = workspaceId,
            Settings = "{\"MaxActiveRooms\":2}"
        };

        _unitOfWork.WorkspaceMemberRepository
            .FirstOrDefaultAsync(
                Arg.Any<Expression<Func<WorkspaceMember, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(member);
        _unitOfWork.WorkspaceRepository
            .GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(workspace);
        _translationRoomClient
            .GetActiveRoomCountAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(2);

        var response = await _service.ValidateMeetingCreation(
            new ValidateMeetingCreationRequest
            {
                WorkspaceId = workspaceId.ToString(),
                UserId = userId.ToString()
            },
            _context);

        Assert.False(response.IsAllowed);
        Assert.Contains("active room limit", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetWorkspaceSettings_ShouldReturnSettings_WhenWorkspaceExists()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var workspace = new Workspace
        {
            Id = workspaceId,
            AllowExternalCollaboration = true,
            Settings = "{\"ArtifactRetentionDays\":15,\"AllowExternalCollaboration\":true}"
        };

        _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(workspace);

        var request = new GetWorkspaceSettingsRequest { WorkspaceId = workspaceId.ToString() };

        // Act
        var response = await _service.GetWorkspaceSettings(request, _context);

        // Assert
        Assert.Equal(15, response.ArtifactRetentionDays);
        Assert.True(response.AllowExternalCollaboration);
    }

    [Fact]
    public async Task GetWorkspaceSettings_ShouldThrowRpcException_WhenWorkspaceDoesNotExist()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns((Workspace)null!);

        var request = new GetWorkspaceSettingsRequest { WorkspaceId = workspaceId.ToString() };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<RpcException>(() => _service.GetWorkspaceSettings(request, _context));
        Assert.Equal(StatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task GetWorkspaceSettings_ShouldDefaultAllowExternalLlmToTrue_WhenAiUsagePolicyNotConfigured()
    {
        // Arrange — opt-out semantics: no AiUsagePolicy at all ⇒ allowed.
        var workspaceId = Guid.NewGuid();
        var workspace = new Workspace
        {
            Id = workspaceId,
            Settings = "{\"ArtifactRetentionDays\":15}"
        };

        _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(workspace);

        var request = new GetWorkspaceSettingsRequest { WorkspaceId = workspaceId.ToString() };

        // Act
        var response = await _service.GetWorkspaceSettings(request, _context);

        // Assert
        Assert.True(response.AllowExternalLlm);
    }

    [Fact]
    public async Task GetWorkspaceSettings_ShouldNormalizeAllowExternalLlmToTrue_WhenPayloadSetsFalse()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var workspace = new Workspace
        {
            Id = workspaceId,
            Settings = "{\"AiUsagePolicy\":{\"AllowExternalLlm\":false}}"
        };

        _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(workspace);

        var request = new GetWorkspaceSettingsRequest { WorkspaceId = workspaceId.ToString() };

        // Act
        var response = await _service.GetWorkspaceSettings(request, _context);

        // Assert
        Assert.True(response.AllowExternalLlm);
    }

    [Fact]
    public async Task GetWorkspaceSettings_ShouldDefaultUseGlobalGlossaryToTrue_WhenAiUsagePolicyNotConfigured()
    {
        // Arrange — opt-out semantics: no AiUsagePolicy at all ⇒ global glossary applies.
        var workspaceId = Guid.NewGuid();
        var workspace = new Workspace
        {
            Id = workspaceId,
            Settings = "{\"ArtifactRetentionDays\":15}"
        };

        _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(workspace);

        var request = new GetWorkspaceSettingsRequest { WorkspaceId = workspaceId.ToString() };

        // Act
        var response = await _service.GetWorkspaceSettings(request, _context);

        // Assert
        Assert.True(response.UseGlobalGlossary);
    }

    [Fact]
    public async Task GetWorkspaceSettings_ShouldReturnUseGlobalGlossaryFalse_WhenWorkspaceOptedOut()
    {
        // Arrange
        var workspaceId = Guid.NewGuid();
        var workspace = new Workspace
        {
            Id = workspaceId,
            Settings = "{\"AiUsagePolicy\":{\"UseGlobalGlossary\":false}}"
        };

        _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(workspace);

        var request = new GetWorkspaceSettingsRequest { WorkspaceId = workspaceId.ToString() };

        // Act
        var response = await _service.GetWorkspaceSettings(request, _context);

        // Assert
        Assert.False(response.UseGlobalGlossary);
    }

    private class TestServerCallContext : ServerCallContext
    {
        private readonly CancellationToken _cancellationToken;

        public TestServerCallContext(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        protected override string MethodCore => "TestMethod";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "127.0.0.1";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => new Metadata();
        protected override CancellationToken CancellationTokenCore => _cancellationToken;
        protected override Metadata ResponseTrailersCore => new Metadata();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => null!;

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => null!;
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}

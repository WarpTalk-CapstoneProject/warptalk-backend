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
    private readonly IBillingSubscriptionClient _billingSubscriptionClient;
    private readonly WorkspaceGrpcService _service;
    private readonly ServerCallContext _context;

    public WorkspaceGrpcServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _authIdentity = Substitute.For<IAuthIdentityClient>();
        _translationRoomClient = Substitute.For<ITranslationRoomClient>();
        _billingSubscriptionClient = Substitute.For<IBillingSubscriptionClient>();

        // WT-262: unless a test says otherwise the workspace is on a plan that comfortably covers
        // whatever it asks for, so the language quota never accidentally explains a failure.
        _billingSubscriptionClient
            .GetWorkspaceFeatureAccessAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceFeatureAccess(HasActiveSubscription: true, MaxLanguages: 3));

        _service = new WorkspaceGrpcService(_unitOfWork, _authIdentity, _translationRoomClient, _billingSubscriptionClient);
        _context = new TestServerCallContext(CancellationToken.None);
    }

    /// <summary>Arranges an active member of a workspace whose own settings permit everything, so a
    /// WT-262 test only exercises the plan quota.</summary>
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

    // ── WT-262: plan max_languages enforcement ────────────────────────────────

    [Fact]
    public async Task ValidateMeetingCreation_ShouldDeny_WhenTargetLanguagesExceedPlanQuota()
    {
        var workspaceId = Guid.NewGuid();
        var userId = ArrangePermittedMember(workspaceId);

        _billingSubscriptionClient
            .GetWorkspaceFeatureAccessAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceFeatureAccess(HasActiveSubscription: true, MaxLanguages: 2));

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

        _billingSubscriptionClient
            .GetWorkspaceFeatureAccessAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceFeatureAccess(HasActiveSubscription: true, MaxLanguages: 3));

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
    /// The regression this ticket must not introduce: an unreachable BillingService returns null,
    /// and the gate has to keep saying "no" rather than degrading to fail-open. WT-249 closed the
    /// bypass on the permission half of this RPC; the quota half must not reopen one.
    /// </summary>
    [Fact]
    public async Task ValidateMeetingCreation_ShouldDeny_WhenBillingIsUnreachable()
    {
        var workspaceId = Guid.NewGuid();
        var userId = ArrangePermittedMember(workspaceId);

        _billingSubscriptionClient
            .GetWorkspaceFeatureAccessAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns((WorkspaceFeatureAccess?)null);

        var request = new ValidateMeetingCreationRequest
        {
            WorkspaceId = workspaceId.ToString(),
            UserId = userId.ToString(),
            TargetLanguages = { "vi", "en" }
        };

        var response = await _service.ValidateMeetingCreation(request, _context);

        Assert.False(response.IsAllowed);
        Assert.NotEmpty(response.ErrorMessage);
    }

    /// <summary>
    /// The bound on that fail-closed behaviour: a single-language meeting cannot exceed any plan,
    /// so billing is never consulted and a billing outage cannot block ordinary meeting creation.
    /// </summary>
    [Fact]
    public async Task ValidateMeetingCreation_ShouldNotConsultBilling_ForASingleTargetLanguage()
    {
        var workspaceId = Guid.NewGuid();
        var userId = ArrangePermittedMember(workspaceId);

        _billingSubscriptionClient
            .GetWorkspaceFeatureAccessAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns((WorkspaceFeatureAccess?)null);

        var request = new ValidateMeetingCreationRequest
        {
            WorkspaceId = workspaceId.ToString(),
            UserId = userId.ToString(),
            TargetLanguages = { "vi" }
        };

        var response = await _service.ValidateMeetingCreation(request, _context);

        Assert.True(response.IsAllowed);
        await _billingSubscriptionClient.DidNotReceive()
            .GetWorkspaceFeatureAccessAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
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

        _billingSubscriptionClient
            .GetWorkspaceFeatureAccessAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceFeatureAccess(HasActiveSubscription: false, MaxLanguages: 1));

        var request = new ValidateMeetingCreationRequest
        {
            WorkspaceId = workspaceId.ToString(),
            UserId = userId.ToString(),
            TargetLanguages = { "vi", "en", "ja" }
        };

        var response = await _service.ValidateMeetingCreation(request, _context);

        Assert.True(response.IsAllowed);
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

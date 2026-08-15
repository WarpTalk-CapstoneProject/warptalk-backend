using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using WarpTalk.WorkspaceService.API.GrpcServices;
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
            IsActive = true,
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
            IsActive = true,
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
        StubWorkspace(workspaceId, new Workspace { Id = workspaceId, IsActive = true, Settings = "{\"MaxActiveRooms\":2}" });
        _translationRoomClient
            .GetActiveRoomCountAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await _service.ValidateMeetingCreationAsync(workspaceId, userId, Array.Empty<string>());

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsAllowed);
        Assert.Contains("active room limit", result.Value.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── WT-263: plan limits enforced from the LOCAL entitlement snapshot ──────
    // These cases arrived from WorkspaceGrpcServiceTests when WT-239 moved the snapshot read and
    // both plan-limit rules off the boundary. They assert the same behaviour against the layer that
    // now owns it.

    /// <summary>Builds the stored snapshot JSON in the shape the entitlements.changed consumer writes.</summary>
    private static string SnapshotJson(params (string Key, string Value, string Source)[] entries) =>
        "{" + string.Join(",", entries.Select(entry =>
            $"\"{entry.Key}\":{{\"value\":\"{entry.Value}\",\"source\":\"{entry.Source}\"}}")) + "}";

    /// <summary>Arranges an active member of a workspace whose own settings permit everything, so a
    /// quota test only exercises the entitlement snapshot.</summary>
    private Guid ArrangePermittedMember(Guid workspaceId, string settings = "{\"MaxActiveRooms\":10}")
    {
        var userId = Guid.NewGuid();

        StubMember(new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            Status = "Active",
            CanCreateMeetings = true
        });

        StubWorkspace(workspaceId, new Workspace { Id = workspaceId, IsActive = true, Settings = settings });

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

    [Fact]
    public async Task ValidateMeetingCreationAsync_ShouldDeny_WhenTargetLanguagesExceedPlanQuota()
    {
        var workspaceId = Guid.NewGuid();
        var userId = ArrangePermittedMember(workspaceId);
        ArrangeSnapshot(workspaceId, SnapshotJson(("max_languages", "2", "plan:startup")));

        var result = await _service.ValidateMeetingCreationAsync(
            workspaceId, userId, new[] { "vi", "en", "ja" });

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsAllowed);
        Assert.Contains("2", result.Value.ErrorMessage);
    }

    [Fact]
    public async Task ValidateMeetingCreationAsync_ShouldAllow_WhenTargetLanguagesFitThePlanQuota()
    {
        var workspaceId = Guid.NewGuid();
        var userId = ArrangePermittedMember(workspaceId);
        ArrangeSnapshot(workspaceId, SnapshotJson(("max_languages", "3", "plan:enterprise")));

        var result = await _service.ValidateMeetingCreationAsync(
            workspaceId, userId, new[] { "vi", "en", "ja" });

        Assert.True(result.Value!.IsAllowed);
    }

    /// <summary>
    /// THE HEADLINE PROOF that the architecture works, and the exact inversion of the WT-262 test it
    /// replaces (ValidateMeetingCreation_ShouldDeny_WhenBillingIsUnreachable).
    ///
    /// There is no billing client to make unreachable any more: neither the gRPC boundary nor the
    /// directory service that now owns the decision takes one, so this test cannot even express
    /// "billing is down" — the dependency is gone. What it asserts is the consequence: a
    /// multi-language meeting is created from the local snapshot alone, with no remote call to
    /// BillingService in the path, so its availability cannot deny it.
    /// </summary>
    [Fact]
    public async Task ValidateMeetingCreationAsync_ShouldAllow_WithBillingUnreachable_BecauseItReadsTheLocalSnapshot()
    {
        var workspaceId = Guid.NewGuid();
        var userId = ArrangePermittedMember(workspaceId);
        ArrangeSnapshot(workspaceId, SnapshotJson(("max_languages", "3", "plan:enterprise")));

        var result = await _service.ValidateMeetingCreationAsync(
            workspaceId, userId, new[] { "vi", "en", "ja" });

        Assert.True(result.Value!.IsAllowed);
        Assert.Empty(result.Value.ErrorMessage);

        // The only collaborators reached are local state and the room count. No billing client
        // exists on either type — asserting that is what pins the stopgap as removed. WT-239 moved
        // the rule, so both constructors are checked; reintroducing the dependency at either layer
        // would restore the coupling WT-263 deleted.
        Assert.DoesNotContain(
            typeof(WorkspaceDirectoryService).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType == typeof(IBillingSubscriptionClient));
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
    public async Task ValidateMeetingCreationAsync_ShouldAllow_OnColdStart_WhenNoSnapshotExistsYet()
    {
        var workspaceId = Guid.NewGuid();
        var userId = ArrangePermittedMember(workspaceId);

        _unitOfWork.WorkspaceEntitlementSnapshotRepository
            .GetForWorkspaceAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns((WorkspaceEntitlementSnapshot?)null);

        var result = await _service.ValidateMeetingCreationAsync(
            workspaceId, userId, new[] { "vi", "en", "ja", "ko" });

        Assert.True(result.Value!.IsAllowed);
    }

    /// <summary>
    /// A workspace with no live plan has no max_languages in force. The workspace's own
    /// AllowedTargetLanguages policy still governs it — this must not become "no subscription, no
    /// meetings".
    /// </summary>
    [Fact]
    public async Task ValidateMeetingCreationAsync_ShouldAllow_WhenWorkspaceHasNoActiveSubscription()
    {
        var workspaceId = Guid.NewGuid();
        var userId = ArrangePermittedMember(workspaceId);
        ArrangeSnapshot(
            workspaceId,
            SnapshotJson(("max_languages", "1", "platform_default")),
            hasActiveSubscription: false);

        var result = await _service.ValidateMeetingCreationAsync(
            workspaceId, userId, new[] { "vi", "en", "ja" });

        Assert.True(result.Value!.IsAllowed);
    }

    /// <summary>
    /// WT-263: max_active_rooms is an ordinary entitlement key now. A resolved workspace_override
    /// beats the settings-JSON copy, with no sentinel value anywhere.
    /// </summary>
    [Fact]
    public async Task ValidateMeetingCreationAsync_ShouldEnforceMaxActiveRooms_FromTheSnapshot()
    {
        var workspaceId = Guid.NewGuid();
        // Settings JSON says 10; the resolved entitlement says the owner tightened to 2.
        var userId = ArrangePermittedMember(workspaceId, "{\"MaxActiveRooms\":10}");
        ArrangeSnapshot(workspaceId, SnapshotJson(("max_active_rooms", "2", "workspace_override")));

        _translationRoomClient
            .GetActiveRoomCountAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await _service.ValidateMeetingCreationAsync(
            workspaceId, userId, Array.Empty<string>());

        Assert.False(result.Value!.IsAllowed);
        Assert.Contains("active room limit (2)", result.Value.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The workspace's own setting is a TIGHTENING and is applied.
    ///
    /// Nothing writes workspace_entitlement_overrides — billing exposes no writer — so the
    /// Settings page's "Max Active Rooms" never reaches the resolver and the snapshot always
    /// answered first. A workspace that lowered its own cap to 2 was still allowed 5.
    /// </summary>
    [Fact]
    public async Task ValidateMeetingCreationAsync_AppliesTheWorkspaceSetting_WhenItIsTighterThanThePlan()
    {
        var workspaceId = Guid.NewGuid();
        var userId = ArrangePermittedMember(workspaceId, "{\"MaxActiveRooms\":2}");
        ArrangeSnapshot(workspaceId, SnapshotJson(("max_active_rooms", "5", "platform_default")));

        _translationRoomClient
            .GetActiveRoomCountAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await _service.ValidateMeetingCreationAsync(
            workspaceId, userId, Array.Empty<string>());

        Assert.False(result.Value!.IsAllowed);
        Assert.Contains("active room limit (2)", result.Value.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plan allows up to 5", result.Value.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The reported bug: the settings page reads 20, room creation refuses at 5, and the message
    /// gave the owner no way to connect the two.
    ///
    /// The setting must NOT raise the ceiling — a workspace may only tighten
    /// (EntitlementConstants.Errors.WorkspaceOverrideLoosens) — but the refusal has to say that
    /// out loud, and name the layer that decided 5. Here that layer is platform_default, which is
    /// what an inactive subscription resolves to however grand the plan on the row.
    /// </summary>
    [Fact]
    public async Task ValidateMeetingCreationAsync_SaysWhyTheSettingDidNotRaiseTheLimit()
    {
        var workspaceId = Guid.NewGuid();
        var userId = ArrangePermittedMember(workspaceId, "{\"MaxActiveRooms\":20}");
        ArrangeSnapshot(workspaceId, SnapshotJson(("max_active_rooms", "5", "platform_default")));

        _translationRoomClient
            .GetActiveRoomCountAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(5);

        var result = await _service.ValidateMeetingCreationAsync(
            workspaceId, userId, Array.Empty<string>());

        Assert.False(result.Value!.IsAllowed);
        var message = result.Value.ErrorMessage;
        Assert.Contains("active room limit (5)", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("platform_default", message, StringComparison.Ordinal);
        Assert.Contains("20", message, StringComparison.Ordinal);
        Assert.Contains("cannot raise it", message, StringComparison.OrdinalIgnoreCase);
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

    // ── Tenant lifecycle: suspending a workspace must stop what costs money ──────
    //
    // Suspension flips is_active and nothing else (AdminWorkspaceService.ChangeLifecycleAsync),
    // so reading that flag is the ONLY way a caller can observe it. ValidateMeetingCreationAsync
    // loaded the workspace and never read it, which is why suspending a workspace blocked document
    // upload and new invitations while meeting creation — and every billable STT/TTS stream a
    // meeting drives — carried on.
    //
    // These arrange a member who would otherwise be allowed through every remaining rule, so the
    // only thing that can deny them is the tenant's own state. Delete the IsLiveTenant check in
    // ValidateMeetingCreationAsync and the two denial cases go red while the allow case stays
    // green.

    /// <summary>
    /// Arranges a member who passes every non-lifecycle rule: active, permitted to create
    /// meetings, in a workspace whose settings and entitlements veto nothing.
    /// </summary>
    private Guid ArrangeOtherwisePermittedMember(Guid workspaceId, bool isActive, DateTime? deletedAt = null)
    {
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
            Name = "Acme",
            IsActive = isActive,
            DeletedAt = deletedAt,
            Settings = "{\"MaxActiveRooms\":10}"
        });

        return userId;
    }

    [Fact]
    public async Task ValidateMeetingCreationAsync_Denies_WhenWorkspaceIsSuspended()
    {
        var workspaceId = Guid.NewGuid();
        var userId = ArrangeOtherwisePermittedMember(workspaceId, isActive: false);

        var result = await _service.ValidateMeetingCreationAsync(workspaceId, userId, new[] { "vi" });

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsAllowed);
        Assert.Contains("suspended", result.Value.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same member in the same workspace, differing ONLY in is_active. Without this the
    /// suspended case above would also pass against a gate that denied everybody.
    /// </summary>
    [Fact]
    public async Task ValidateMeetingCreationAsync_Allows_WhenWorkspaceIsActive()
    {
        var workspaceId = Guid.NewGuid();
        var userId = ArrangeOtherwisePermittedMember(workspaceId, isActive: true);

        var result = await _service.ValidateMeetingCreationAsync(workspaceId, userId, new[] { "vi" });

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsAllowed);
        Assert.Empty(result.Value.ErrorMessage);
    }

    /// <summary>
    /// A soft-deleted workspace has left the lifecycle entirely. AdminWorkspaceService refuses to
    /// suspend or reactivate one, so its is_active is frozen at whatever it held when it was
    /// deleted — an active-then-deleted workspace would sail through a check that read is_active
    /// alone.
    /// </summary>
    [Fact]
    public async Task ValidateMeetingCreationAsync_Denies_WhenWorkspaceIsSoftDeleted()
    {
        var workspaceId = Guid.NewGuid();
        var userId = ArrangeOtherwisePermittedMember(
            workspaceId, isActive: true, deletedAt: DateTime.UtcNow);

        var result = await _service.ValidateMeetingCreationAsync(workspaceId, userId, new[] { "vi" });

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsAllowed);
        Assert.Contains("suspended", result.Value.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Suspension is a decision about the TENANT, so it must not depend on the caller's own
    /// permissions being intact. A member who could create meetings yesterday and a member whose
    /// permission was revoked both get stopped — this pins that the lifecycle check runs even when
    /// the workspace's own settings would have vetoed the languages anyway, i.e. it is not
    /// accidentally shadowed by a later rule.
    /// </summary>
    [Fact]
    public async Task ValidateMeetingCreationAsync_Denies_WhenSuspended_BeforeConsultingEntitlements()
    {
        var workspaceId = Guid.NewGuid();
        var userId = ArrangeOtherwisePermittedMember(workspaceId, isActive: false);

        var result = await _service.ValidateMeetingCreationAsync(workspaceId, userId, new[] { "vi" });

        Assert.False(result.Value!.IsAllowed);
        // The tenant is not entitled to anything, so nothing downstream is even asked. A denial
        // that still burned a snapshot read and a cross-service room count would work, but it
        // would mean the check had been bolted on at the end rather than answered first.
        await _unitOfWork.WorkspaceEntitlementSnapshotRepository
            .DidNotReceiveWithAnyArgs()
            .GetForWorkspaceAsync(default, default);
        await _translationRoomClient
            .DidNotReceiveWithAnyArgs()
            .GetActiveRoomCountAsync(default, default);
    }

    [Fact]
    public async Task GetPreflightAsync_ReportsInactive_WhenWorkspaceIsSuspended()
    {
        var workspaceId = Guid.NewGuid();
        StubWorkspace(workspaceId, new Workspace
        {
            Id = workspaceId,
            Name = "Acme",
            Slug = "acme",
            IsActive = false,
            Settings = "{}"
        });

        var result = await _service.GetPreflightAsync(workspaceId, userEmail: null);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsActive);
    }

    /// <summary>
    /// TranslationRoomService calls this RPC with an empty email purely to learn whether the tenant
    /// is live, on the room join and start paths. That is only affordable because an absent email
    /// skips the verified-domain query entirely — if that ever stopped being true, every join in
    /// the product would start paying for a lookup it has no use for.
    /// </summary>
    [Fact]
    public async Task GetPreflightAsync_SkipsTheVerifiedDomainLookup_WhenNoEmailIsSupplied()
    {
        var workspaceId = Guid.NewGuid();
        StubWorkspace(workspaceId, new Workspace
        {
            Id = workspaceId,
            Name = "Acme",
            Slug = "acme",
            IsActive = true,
            Settings = "{}"
        });

        var result = await _service.GetPreflightAsync(workspaceId, userEmail: null);

        Assert.True(result.Value!.IsActive);
        Assert.False(result.Value.IsDomainMatched);
        await _unitOfWork.WorkspaceVerifiedDomainRepository
            .DidNotReceiveWithAnyArgs()
            .AnyAsync(default!, default);
    }
}

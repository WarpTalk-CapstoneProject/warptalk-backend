using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using WarpTalk.Shared.Protos;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Entitlements;

namespace WarpTalk.WorkspaceService.API.GrpcServices;

public class WorkspaceGrpcService : WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceBase
{
    // WT-263: LanguageCountAlwaysWithinPlan is gone with the call it protected. It existed to bound
    // the blast radius of a fail-closed billing round-trip by skipping the round-trip for
    // single-language meetings. There is no round-trip left to skip, so every request is now checked
    // against the same local snapshot and the carve-out has nothing to bound.

    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly ITranslationRoomClient _translationRoomClient;

    public WorkspaceGrpcService(
        IUnitOfWork unitOfWork,
        IAuthIdentityClient authIdentity,
        ITranslationRoomClient translationRoomClient)
    {
        _unitOfWork = unitOfWork;
        _authIdentity = authIdentity;
        _translationRoomClient = translationRoomClient;
    }

    public override async Task<GetWorkspaceMemberResponse> GetWorkspaceMemberDetails(
        GetWorkspaceMemberRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;

        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId) ||
            !Guid.TryParse(request.UserId, out var userId))
        {
            return new GetWorkspaceMemberResponse { IsMember = false };
        }

        var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
            m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);

        if (member == null)
        {
            return new GetWorkspaceMemberResponse { IsMember = false };
        }

        var roleName = await _authIdentity.GetRoleNameByIdAsync(member.RoleId, ct);

        return new GetWorkspaceMemberResponse
        {
            IsMember = true,
            RoleName = roleName ?? "Member",
            MembershipType = member.MembershipType ?? "internal",
            IsActive = string.Equals(member.Status, "active", StringComparison.OrdinalIgnoreCase),
            CanCreateMeetings = member.CanCreateMeetings
        };
    }

    public override async Task<GetWorkspaceNamesResponse> GetWorkspaceNames(
        GetWorkspaceNamesRequest request,
        ServerCallContext context)
    {
        var workspaceIds = request.WorkspaceIds
            .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        var response = new GetWorkspaceNamesResponse();
        if (workspaceIds.Length == 0)
            return response;

        var workspaces = await _unitOfWork.WorkspaceRepository.FindAsync(
            workspace => workspaceIds.Contains(workspace.Id),
            "",
            context.CancellationToken);
        response.Workspaces.AddRange(workspaces.Select(workspace => new WorkspaceNameItem
        {
            WorkspaceId = workspace.Id.ToString(),
            WorkspaceName = workspace.Name
        }));
        return response;
    }

    public override async Task<ValidateMeetingCreationResponse> ValidateMeetingCreation(
        ValidateMeetingCreationRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;

        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId) ||
            !Guid.TryParse(request.UserId, out var userId))
        {
            return new ValidateMeetingCreationResponse
            {
                IsAllowed = false,
                ErrorMessage = "Invalid WorkspaceId or UserId format."
            };
        }

        var member = await _unitOfWork.WorkspaceMemberRepository.FirstOrDefaultAsync(
            m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null, "", ct);

        if (member == null)
        {
            return new ValidateMeetingCreationResponse
            {
                IsAllowed = false,
                ErrorMessage = "User is not a member of this workspace."
            };
        }

        if (!string.Equals(member.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            return new ValidateMeetingCreationResponse
            {
                IsAllowed = false,
                ErrorMessage = "Workspace member is inactive."
            };
        }

        if (!member.CanCreateMeetings)
        {
            return new ValidateMeetingCreationResponse
            {
                IsAllowed = false,
                ErrorMessage = "User does not have permission to create meetings."
            };
        }

        var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
        if (workspace == null)
        {
            return new ValidateMeetingCreationResponse
            {
                IsAllowed = false,
                ErrorMessage = "Workspace not found."
            };
        }

        var config = WorkspaceHelper.GetWorkspaceConfig(workspace);

        // Validate target languages subset
        if (request.TargetLanguages != null && request.TargetLanguages.Any())
        {
            if (config.AllowedTargetLanguages != null && config.AllowedTargetLanguages.Any())
            {
                var unsupported = request.TargetLanguages.FirstOrDefault(lang =>
                    !config.AllowedTargetLanguages.Contains(lang, StringComparer.OrdinalIgnoreCase));
                if (unsupported != null)
                {
                    return new ValidateMeetingCreationResponse
                    {
                        IsAllowed = false,
                        ErrorMessage = $"Target language '{unsupported}' is not allowed by the workspace policy."
                    };
                }
            }
        }

        // WT-263: ONE local read serves every plan-derived limit below. No network call is made from
        // here on, which is why a BillingService outage can no longer affect meeting creation.
        var snapshot = await _unitOfWork.WorkspaceEntitlementSnapshotRepository
            .GetForWorkspaceAsync(workspaceId, ct);
        var entitlements = snapshot == null
            ? WorkspaceEntitlements.Unknown
            : WorkspaceEntitlements.FromSnapshot(snapshot.EntitlementsJson, snapshot.HasActiveSubscription);

        var activeRoomLimit = ResolveMaxActiveRooms(entitlements, config);
        if (activeRoomLimit > 0)
        {
            var activeRoomCount = await _translationRoomClient.GetActiveRoomCountAsync(workspaceId, ct);
            if (activeRoomCount >= activeRoomLimit)
            {
                return new ValidateMeetingCreationResponse
                {
                    IsAllowed = false,
                    ErrorMessage = $"Workspace active room limit ({activeRoomLimit}) has been reached."
                };
            }
        }

        var planLanguageCheck = ValidatePlanLanguageQuota(entitlements, request.TargetLanguages?.Count ?? 0);
        if (planLanguageCheck != null)
        {
            return planLanguageCheck;
        }

        return new ValidateMeetingCreationResponse
        {
            IsAllowed = true,
            ErrorMessage = ""
        };
    }

    /// <summary>
    /// WT-263: enforces <c>max_languages</c> from the LOCAL entitlement snapshot. Returns null when
    /// the request clears the quota, or a denial when it does not.
    ///
    /// Synchronous and non-async, because there is nothing left to await. WT-262 phase 1 called
    /// BillingService here and had to fail closed on an outage — it could not tell "unknown" from
    /// "allowed", and a quota with no after-the-fact remedy cannot be handed out on a guess. That
    /// call, its fail-closed branch, and the single-target-language carve-out that bounded its blast
    /// radius are all deleted: the value is replicated ahead of time, so the question is answered
    /// from this service's own database and BillingService's availability never enters into it.
    ///
    /// What is NOT changed: the workspace-permission gate above still fails closed (WT-249). That
    /// one is a permission decision with no local replica, and it must stay that way.
    ///
    /// A null limit means the quota is not in force — cold start, or no live subscription. Neither
    /// is a denial; the workspace's own AllowedTargetLanguages policy governs those cases, exactly
    /// as it did before WT-262. See WorkspaceEntitlements for the cold-start reasoning.
    /// </summary>
    private static ValidateMeetingCreationResponse? ValidatePlanLanguageQuota(
        WorkspaceEntitlements entitlements,
        int requestedLanguageCount)
    {
        if (requestedLanguageCount <= 0)
        {
            return null;
        }

        var maxLanguages = entitlements.Limit(EntitlementKeys.MaxLanguages);
        if (maxLanguages is null or <= 0)
        {
            return null;
        }

        if (requestedLanguageCount > maxLanguages.Value)
        {
            return new ValidateMeetingCreationResponse
            {
                IsAllowed = false,
                ErrorMessage = $"Your plan allows {maxLanguages.Value} target language(s) per meeting; {requestedLanguageCount} were requested."
            };
        }

        return null;
    }

    /// <summary>
    /// WT-263: <c>max_active_rooms</c> is now an ordinary entitlement key — no sentinel, no special
    /// case.
    ///
    /// The old design called for a <c>-1</c> "inherit from plan" sentinel in the settings JSON.
    /// Provenance replaces it: an owner-set value arrives resolved with source
    /// <c>workspace_override</c>, an unset one resolves from the plan, and neither the caller nor
    /// the storage needs a magic number to tell them apart. The resolver has already clamped an
    /// owner's value to the plan ceiling, so whatever arrives here is enforceable as-is.
    ///
    /// The settings-JSON value remains the fallback for cold start only. It is where every existing
    /// workspace's number lives today, and dropping straight to it keeps behaviour identical for a
    /// workspace whose snapshot has not arrived — the same rule the pre-WT-263 code applied.
    /// </summary>
    private static int ResolveMaxActiveRooms(
        WorkspaceEntitlements entitlements,
        Domain.Settings.WorkspaceConfiguration config)
    {
        var resolved = entitlements.SelfServiceLimit(EntitlementKeys.MaxActiveRooms);
        if (resolved.HasValue)
        {
            return (int)Math.Clamp(resolved.Value, int.MinValue, int.MaxValue);
        }

        return config.MaxActiveRooms;
    }

    public override async Task<GetWorkspaceSettingsResponse> GetWorkspaceSettings(
        GetWorkspaceSettingsRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;

        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid WorkspaceId format."));
        }

        var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
        if (workspace == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Workspace not found."));
        }

        var config = WorkspaceHelper.GetWorkspaceConfig(workspace);

        return new GetWorkspaceSettingsResponse
        {
            ArtifactRetentionDays = config.ArtifactRetentionDays,
            AllowExternalCollaboration = config.AllowExternalCollaboration,
            IsProfanityFilterEnabled = config.IsProfanityFilterEnabled,
            // Opt-out semantics: unset at workspace level ⇒ allowed. Mirrors the fallback
            // DocumentSecurityGuardrailConsumerService.ResolvePolicySettingsAsync already
            // applies for documents.
            AllowExternalLlm = config.AiUsagePolicy?.AllowExternalLlm ?? true,
            UseGlobalGlossary = config.AiUsagePolicy?.UseGlobalGlossary ?? true
        };
    }

    public override async Task<GetWorkspacePreflightResponse> GetWorkspacePreflightDetails(
        GetWorkspacePreflightRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;

        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid WorkspaceId format."));
        }

        var workspace = await _unitOfWork.WorkspaceRepository.GetByIdAsync(workspaceId, ct);
        if (workspace == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Workspace not found."));
        }

        var config = WorkspaceHelper.GetWorkspaceConfig(workspace);

        bool isDomainMatched = false;
        if (!string.IsNullOrWhiteSpace(request.UserEmail))
        {
            if (WarpTalk.WorkspaceService.Domain.ValueObjects.EmailAddress.TryParse(request.UserEmail, out var emailAddress) && emailAddress != null)
            {
                var domain = emailAddress.Domain;
                isDomainMatched = await _unitOfWork.WorkspaceVerifiedDomainRepository.AnyAsync(
                    vd => vd.WorkspaceId == workspaceId
                          && vd.Domain.ToLower() == domain.ToLower()
                          && vd.Status == "verified"
                          && vd.VerifiedAt != null
                          && vd.RevokedAt == null,
                    ct);
            }
        }

        return new GetWorkspacePreflightResponse
        {
            IsActive = workspace.IsActive && workspace.DeletedAt == null,
            WorkspaceName = workspace.Name,
            WorkspaceSlug = workspace.Slug,
            IsDomainMatched = isDomainMatched,
            AllowExternalCollaboration = config.AllowExternalCollaboration
        };
    }
}

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using WarpTalk.Shared.Protos;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Helpers;

namespace WarpTalk.WorkspaceService.API.GrpcServices;

public class WorkspaceGrpcService : WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceBase
{
    /// <summary>
    /// WT-262. Below this many target languages no plan can possibly forbid the request — the
    /// smallest max_languages an admin may store is 1 (PlanService validation) — so the billing
    /// round-trip is skipped entirely. This is what keeps the fail-closed branch below from turning
    /// a billing outage into "nobody can start a meeting": ordinary single-language creation never
    /// touches BillingService at all.
    /// </summary>
    private const int LanguageCountAlwaysWithinPlan = 1;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuthIdentityClient _authIdentity;
    private readonly ITranslationRoomClient _translationRoomClient;
    private readonly IBillingSubscriptionClient _billingSubscriptionClient;

    public WorkspaceGrpcService(
        IUnitOfWork unitOfWork,
        IAuthIdentityClient authIdentity,
        ITranslationRoomClient translationRoomClient,
        IBillingSubscriptionClient billingSubscriptionClient)
    {
        _unitOfWork = unitOfWork;
        _authIdentity = authIdentity;
        _translationRoomClient = translationRoomClient;
        _billingSubscriptionClient = billingSubscriptionClient;
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

        var activeRoomCount = await _translationRoomClient.GetActiveRoomCountAsync(workspaceId, ct);
        if (config.MaxActiveRooms > 0 && activeRoomCount >= config.MaxActiveRooms)
        {
            return new ValidateMeetingCreationResponse
            {
                IsAllowed = false,
                ErrorMessage = $"Workspace active room limit ({config.MaxActiveRooms}) has been reached."
            };
        }

        var planLanguageCheck = await ValidatePlanLanguageQuotaAsync(workspaceId, request.TargetLanguages?.Count ?? 0, ct);
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
    /// WT-262: enforces the subscription plan's <c>max_languages</c>. Returns null when the request
    /// clears the quota, or a denial response when it does not.
    ///
    /// FAIL-CLOSED, deliberately, and this is the one contentious call in WT-262. It adds a second
    /// remote dependency (BillingService) behind a gate whose first dependency already fails closed:
    /// IWorkspaceMeetingPolicy is documented fail-closed and WorkspaceMeetingPolicyGrpcClient turns
    /// any transport exception into ServiceUnavailable, on purpose, because this RPC *is* the
    /// permission gate and an outage that let creations through would reopen WT-249. Answering
    /// "allowed" here on a billing outage would make one half of this response trustworthy and the
    /// other half not, which is not a contract a caller can reason about. The quota being protected
    /// also has no after-the-fact remedy: once a room is created with five languages, nothing
    /// revokes them, so a fail-open window is simply free paid capacity.
    ///
    /// Note that BillingSubscriptionGrpcClient's other method fails OPEN. That is not an
    /// inconsistency being ignored — it is the difference between a check that widens (the trial
    /// invite cap) and one that narrows. See IBillingSubscriptionClient.
    ///
    /// The cost of failing closed is bounded on purpose rather than accepted wholesale: billing is
    /// consulted ONLY when the request carries more than one target language, so a billing outage
    /// degrades multi-language meeting creation and leaves every ordinary meeting untouched.
    /// </summary>
    private async Task<ValidateMeetingCreationResponse?> ValidatePlanLanguageQuotaAsync(
        Guid workspaceId,
        int requestedLanguageCount,
        CancellationToken ct)
    {
        if (requestedLanguageCount <= LanguageCountAlwaysWithinPlan)
        {
            return null;
        }

        var featureAccess = await _billingSubscriptionClient.GetWorkspaceFeatureAccessAsync(workspaceId, ct);

        if (featureAccess == null)
        {
            return new ValidateMeetingCreationResponse
            {
                IsAllowed = false,
                ErrorMessage = "Could not verify your plan's language limit right now. Please try again in a moment, or start the meeting with a single target language."
            };
        }

        // No live plan means there is no max_languages in force to enforce. The workspace-level
        // AllowedTargetLanguages policy checked above is what governs those workspaces, exactly as
        // it did before WT-262 — this must not become an accidental "no subscription, no meetings".
        if (!featureAccess.HasActiveSubscription)
        {
            return null;
        }

        if (featureAccess.MaxLanguages > 0 && requestedLanguageCount > featureAccess.MaxLanguages)
        {
            return new ValidateMeetingCreationResponse
            {
                IsAllowed = false,
                ErrorMessage = $"Your plan allows {featureAccess.MaxLanguages} target language(s) per meeting; {requestedLanguageCount} were requested."
            };
        }

        return null;
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

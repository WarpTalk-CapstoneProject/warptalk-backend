using System;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using WarpTalk.Shared.Protos;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Helpers;

namespace WarpTalk.WorkspaceService.API.GrpcServices;

public class WorkspaceGrpcService : WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceBase
{
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

        var activeRoomCount = await _translationRoomClient.GetActiveRoomCountAsync(workspaceId, ct);
        if (config.MaxActiveRooms > 0 && activeRoomCount >= config.MaxActiveRooms)
        {
            return new ValidateMeetingCreationResponse
            {
                IsAllowed = false,
                ErrorMessage = $"Workspace active room limit ({config.MaxActiveRooms}) has been reached."
            };
        }

        return new ValidateMeetingCreationResponse
        {
            IsAllowed = true,
            ErrorMessage = ""
        };
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
                isDomainMatched = await _unitOfWork.Repository<WarpTalk.WorkspaceService.Domain.Entities.WorkspaceVerifiedDomain>().AnyAsync(
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

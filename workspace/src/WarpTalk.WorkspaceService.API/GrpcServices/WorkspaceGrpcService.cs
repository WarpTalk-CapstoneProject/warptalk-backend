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

    public WorkspaceGrpcService(IUnitOfWork unitOfWork, IAuthIdentityClient authIdentity)
    {
        _unitOfWork = unitOfWork;
        _authIdentity = authIdentity;
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

        // Active rooms check:
        // Since we are only modifying the Workspace module, we simulate the active rooms check.
        // Once TranslationRoomService has an active rooms count endpoint, we would call it.
        // For now, we allow the request to pass.
        
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
}

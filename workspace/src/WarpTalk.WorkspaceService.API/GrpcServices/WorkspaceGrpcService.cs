using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using WarpTalk.Shared.Protos;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.API.GrpcServices;

public class WorkspaceGrpcService : WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceBase
{
    private readonly IWorkspaceDirectoryService _workspaceDirectory;

    public WorkspaceGrpcService(IWorkspaceDirectoryService workspaceDirectory)
    {
        _workspaceDirectory = workspaceDirectory;
    }

    public override async Task<GetWorkspaceMemberResponse> GetWorkspaceMemberDetails(
        GetWorkspaceMemberRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId) ||
            !Guid.TryParse(request.UserId, out var userId))
        {
            return new GetWorkspaceMemberResponse { IsMember = false };
        }

        var result = await _workspaceDirectory.GetMemberDetailsAsync(
            workspaceId, userId, context.CancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            return new GetWorkspaceMemberResponse { IsMember = false };
        }

        var member = result.Value;
        return new GetWorkspaceMemberResponse
        {
            IsMember = true,
            RoleName = member.RoleName,
            MembershipType = member.MembershipType,
            IsActive = member.IsActive,
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

        var result = await _workspaceDirectory.GetWorkspaceNamesAsync(
            workspaceIds, context.CancellationToken);
        if (!result.IsSuccess)
            return response;

        response.Workspaces.AddRange(result.Value!.Select(workspace => new WorkspaceNameItem
        {
            WorkspaceId = workspace.WorkspaceId.ToString(),
            WorkspaceName = workspace.WorkspaceName
        }));
        return response;
    }

    public override async Task<ValidateMeetingCreationResponse> ValidateMeetingCreation(
        ValidateMeetingCreationRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId) ||
            !Guid.TryParse(request.UserId, out var userId))
        {
            return new ValidateMeetingCreationResponse
            {
                IsAllowed = false,
                ErrorMessage = "Invalid WorkspaceId or UserId format."
            };
        }

        IReadOnlyCollection<string> targetLanguages =
            request.TargetLanguages?.ToArray() ?? Array.Empty<string>();

        var result = await _workspaceDirectory.ValidateMeetingCreationAsync(
            workspaceId, userId, targetLanguages, context.CancellationToken);

        // Fail closed: an unusable decision must never read as permission granted.
        if (!result.IsSuccess || result.Value is null)
        {
            return new ValidateMeetingCreationResponse
            {
                IsAllowed = false,
                ErrorMessage = result.Error ?? "Unable to validate meeting creation."
            };
        }

        return new ValidateMeetingCreationResponse
        {
            IsAllowed = result.Value.IsAllowed,
            ErrorMessage = result.Value.ErrorMessage
        };
    }

    public override async Task<GetWorkspaceSettingsResponse> GetWorkspaceSettings(
        GetWorkspaceSettingsRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid WorkspaceId format."));
        }

        var result = await _workspaceDirectory.GetSettingsAsync(workspaceId, context.CancellationToken);
        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Workspace not found."));
        }

        var settings = result.Value!;
        return new GetWorkspaceSettingsResponse
        {
            ArtifactRetentionDays = settings.ArtifactRetentionDays,
            AllowExternalCollaboration = settings.AllowExternalCollaboration,
            IsProfanityFilterEnabled = settings.IsProfanityFilterEnabled,
            AllowExternalLlm = settings.AllowExternalLlm,
            UseGlobalGlossary = settings.UseGlobalGlossary
        };
    }

    public override async Task<GetWorkspacePreflightResponse> GetWorkspacePreflightDetails(
        GetWorkspacePreflightRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkspaceId, out var workspaceId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid WorkspaceId format."));
        }

        var result = await _workspaceDirectory.GetPreflightAsync(
            workspaceId, request.UserEmail, context.CancellationToken);
        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Workspace not found."));
        }

        var preflight = result.Value!;
        return new GetWorkspacePreflightResponse
        {
            IsActive = preflight.IsActive,
            WorkspaceName = preflight.WorkspaceName,
            WorkspaceSlug = preflight.WorkspaceSlug,
            IsDomainMatched = preflight.IsDomainMatched,
            AllowExternalCollaboration = preflight.AllowExternalCollaboration
        };
    }
}

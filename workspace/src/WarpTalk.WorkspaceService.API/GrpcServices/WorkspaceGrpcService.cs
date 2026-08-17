using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using WarpTalk.Shared.Protos;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.API.GrpcServices;

public class WorkspaceGrpcService : WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceBase
{
    // WT-263: LanguageCountAlwaysWithinPlan is gone with the call it protected. It existed to bound
    // the blast radius of a fail-closed billing round-trip by skipping the round-trip for
    // single-language meetings. There is no round-trip left to skip, so every request is now checked
    // against the same local snapshot and the carve-out has nothing to bound.
    //
    // WT-239: the snapshot read and both plan-limit rules moved with the rest of the decision into
    // WorkspaceDirectoryService. No IBillingSubscriptionClient here — and none there either; the
    // entitlement value is replicated ahead of time and read locally.

    private readonly IWorkspaceDirectoryService _workspaceDirectory;
    private readonly IWorkspaceCoMembershipService _coMembership;

    public WorkspaceGrpcService(
        IWorkspaceDirectoryService workspaceDirectory,
        IWorkspaceCoMembershipService coMembership)
    {
        _workspaceDirectory = workspaceDirectory;
        _coMembership = coMembership;
    }

    /// <summary>
    /// WT-335: the Gateway's presence query needs to know which of the users it was asked about the
    /// caller may see at all. Answered as a batch intersection because the Gateway arrives with up
    /// to 500 ids at once and presence sits on a hot path.
    ///
    /// Unparseable ids are dropped rather than rejected: this is a privacy filter, so anything it
    /// cannot positively resolve to a shared workspace must simply not come back. Failing the whole
    /// call for one bad id would also hand a caller a way to distinguish "bad id" from "not yours".
    /// </summary>
    public override async Task<GetSharedWorkspaceMembersResponse> GetSharedWorkspaceMembers(
        GetSharedWorkspaceMembersRequest request,
        ServerCallContext context)
    {
        var response = new GetSharedWorkspaceMembersResponse();

        if (!Guid.TryParse(request.CallerUserId, out var callerUserId))
        {
            return response;
        }

        var candidates = new List<Guid>(request.CandidateUserIds.Count);
        foreach (var raw in request.CandidateUserIds)
        {
            if (Guid.TryParse(raw, out var candidateId))
            {
                candidates.Add(candidateId);
            }
        }

        if (candidates.Count == 0)
        {
            return response;
        }

        var result = await _coMembership.GetVisibleCoMemberIdsAsync(
            callerUserId, candidates, context.CancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            // Fail CLOSED: an empty list means "show nobody as visible", which the Gateway renders
            // as everyone Offline. Returning the full candidate set on error would restore exactly
            // the leak this closes, at the worst possible moment.
            return response;
        }

        foreach (var userId in result.Value)
        {
            response.VisibleUserIds.Add(userId.ToString());
        }

        return response;
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
            workspaceId, userId, targetLanguages, request.SourceLanguage, context.CancellationToken);

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
        var response = new GetWorkspaceSettingsResponse
        {
            ArtifactRetentionDays = settings.ArtifactRetentionDays,
            AllowExternalCollaboration = settings.AllowExternalCollaboration,
            IsProfanityFilterEnabled = settings.IsProfanityFilterEnabled,
            AllowExternalLlm = settings.AllowExternalLlm,
            UseGlobalGlossary = settings.UseGlobalGlossary
        };
        response.AllowedTargetLanguages.AddRange(settings.AllowedTargetLanguages);
        return response;
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

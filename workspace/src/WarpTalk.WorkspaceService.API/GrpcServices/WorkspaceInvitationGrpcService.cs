using System;
using System.Threading.Tasks;
using Grpc.Core;
using WarpTalk.Shared.Protos;
using WarpTalk.WorkspaceService.Application.Interfaces;
using GrpcAcceptInvitationRequest = WarpTalk.Shared.Protos.AcceptInvitationRequest;
using DtoAcceptInvitationRequest = WarpTalk.WorkspaceService.Application.DTOs.WorkspaceInvitation.AcceptInvitationRequest;

namespace WarpTalk.WorkspaceService.API.GrpcServices;

public class WorkspaceInvitationGrpcService : WorkspaceInvitationService.WorkspaceInvitationServiceBase
{
    private readonly IWorkspaceInvitationService _invitationService;

    public WorkspaceInvitationGrpcService(IWorkspaceInvitationService invitationService)
    {
        _invitationService = invitationService;
    }

    public override async Task<VerifyInvitationTokenResponse> VerifyInvitationToken(
        VerifyInvitationTokenRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Token is required."));
        }

        var result = await _invitationService.VerifyInvitationTokenInternalAsync(request.Token, ct);

        if (!result.IsSuccess)
        {
            return new VerifyInvitationTokenResponse
            {
                IsValid = false,
                ErrorMessage = result.Error
            };
        }

        var data = result.Value!;
        return new VerifyInvitationTokenResponse
        {
            IsValid = true,
            Email = data.Email,
            WorkspaceId = data.WorkspaceId.ToString(),
            WorkspaceName = data.WorkspaceName,
            RoleId = data.RoleId.ToString(),
            RoleName = data.RoleName,
            MembershipType = data.MembershipType,
            ErrorMessage = ""
        };
    }

    public override async Task<AcceptInvitationResponse> AcceptInvitation(
        GrpcAcceptInvitationRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Token is required."));
        }
        if (!Guid.TryParse(request.UserId, out var userId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid UserId."));
        }
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Email is required."));
        }

        var result = await _invitationService.AcceptInvitationAsync(
            new DtoAcceptInvitationRequest(request.Token),
            userId,
            request.Email,
            ct);

        if (!result.IsSuccess)
        {
            return new AcceptInvitationResponse
            {
                Success = false,
                ErrorMessage = result.Error
            };
        }

        return new AcceptInvitationResponse
        {
            Success = true,
            ErrorMessage = ""
        };
    }
}


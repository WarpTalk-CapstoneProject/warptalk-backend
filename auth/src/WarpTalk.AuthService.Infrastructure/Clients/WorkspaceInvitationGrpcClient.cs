using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared.Protos;
using WarpTalk.AuthService.Infrastructure.Mappers;

namespace WarpTalk.AuthService.Infrastructure.Clients;

public class WorkspaceInvitationGrpcClient : IWorkspaceInvitationClient
{
    private readonly WorkspaceInvitationService.WorkspaceInvitationServiceClient _client;
    private readonly ILogger<WorkspaceInvitationGrpcClient> _logger;

    public WorkspaceInvitationGrpcClient(
        WorkspaceInvitationService.WorkspaceInvitationServiceClient client,
        ILogger<WorkspaceInvitationGrpcClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<VerifyInvitationResult> VerifyInvitationTokenAsync(string token, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.VerifyInvitationTokenAsync(
                new VerifyInvitationTokenRequest { Token = token },
                cancellationToken: ct);

            return WorkspaceInvitationMapper.ToResult(response);
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "gRPC VerifyInvitationToken RpcException. Status: {Status}", ex.Status);
            return new VerifyInvitationResult(false, null, null, null, null, null, null, $"gRPC error: {ex.Status.Detail}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gRPC VerifyInvitationToken failed.");
            return new VerifyInvitationResult(false, null, null, null, null, null, null, "Failed to connect to workspace service.");
        }
    }

    public async Task<AcceptInvitationResult> AcceptInvitationAsync(string token, Guid userId, string email, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.AcceptInvitationAsync(
                new AcceptInvitationRequest
                {
                    Token = token,
                    UserId = userId.ToString(),
                    Email = email
                },
                cancellationToken: ct);

            return WorkspaceInvitationMapper.ToResult(response);
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "gRPC AcceptInvitation RpcException. Status: {Status}", ex.Status);
            return new AcceptInvitationResult(false, $"gRPC error: {ex.Status.Detail}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gRPC AcceptInvitation failed.");
            return new AcceptInvitationResult(false, "Failed to connect to workspace service.");
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Protos;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;

namespace WarpTalk.WorkspaceService.Infrastructure.Clients;

public class AuthIdentityGrpcClient : IAuthIdentityClient
{
    private readonly UserService.UserServiceClient _client;
    private readonly ILogger<AuthIdentityGrpcClient> _logger;

    public AuthIdentityGrpcClient(UserService.UserServiceClient client, ILogger<AuthIdentityGrpcClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<User?> GetUserByIdAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetUserByIdAsync(
                new GetUserRequest { Id = userId.ToString() },
                cancellationToken: ct);
            return MapUser(response);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auth gRPC GetUserById failed. UserId: {UserId}", userId);
            return null;
        }
    }

    public async Task<User?> GetUserByEmailAsync(string email, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetUserByEmailAsync(
                new GetUserByEmailRequest { Email = email },
                cancellationToken: ct);
            return MapUser(response);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auth gRPC GetUserByEmail failed. Email: {Email}", email);
            return null;
        }
    }

    public async Task<Role?> GetRoleByIdAsync(Guid roleId, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetRoleByIdAsync(
                new GetRoleByIdRequest { Id = roleId.ToString() },
                cancellationToken: ct);
            return MapRole(response);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auth gRPC GetRoleById failed. RoleId: {RoleId}", roleId);
            return null;
        }
    }

    public async Task<Role?> GetRoleByNameAsync(string roleName, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetRoleByNameAsync(
                new GetRoleByNameRequest { Name = roleName },
                cancellationToken: ct);
            return MapRole(response);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auth gRPC GetRoleByName failed. RoleName: {RoleName}", roleName);
            return null;
        }
    }

    private static User MapUser(GetUserResponse response) => new()
    {
        Id = Guid.Parse(response.Id),
        Email = response.Email,
        FullName = response.FullName,
        AvatarUrl = string.IsNullOrEmpty(response.AvatarUrl) ? null : response.AvatarUrl,
        PreferredLanguage = string.IsNullOrWhiteSpace(response.PreferredLanguage) ? "en" : response.PreferredLanguage
    };

    private static Role MapRole(GetRoleResponse response) => new()
    {
        Id = Guid.Parse(response.Id),
        Name = response.Name,
        Description = string.IsNullOrEmpty(response.Description) ? null : response.Description
    };
}

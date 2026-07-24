using System;
using System.Threading.Tasks;
using Grpc.Core;
using WarpTalk.Shared.Extensions;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.API.Extensions;

public static class GrpcAuthorizationExtensions
{
    public static async Task AuthorizeWorkspaceAsync(
        this IWorkspaceAuthorizationService workspaceAuthService,
        Guid workspaceId, 
        ServerCallContext context, 
        string allowedRoles = "Owner, Admin")
    {
        var httpContext = context.GetHttpContext();
        var userId = httpContext.User.GetUserId();
        
        if (userId == null)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, BillingMessageConstants.Grpc.AuthenticationRequired));
        }

        var authResult = await workspaceAuthService.AuthorizeAsync(workspaceId, userId.Value, allowedRoles, context.CancellationToken);
        if (!authResult.IsSuccess)
        {
            var statusCode = authResult.ErrorCode == "FORBIDDEN" ? StatusCode.PermissionDenied : StatusCode.Internal;
            throw new RpcException(new Status(statusCode, authResult.Error ?? BillingMessageConstants.Grpc.AccessDenied));
        }
    }
}

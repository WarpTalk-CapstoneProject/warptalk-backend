using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Threading.Tasks;
using System.Text.Json;

namespace WarpTalk.TranslationRoomService.API.Interceptors;

/// <summary>
/// Enforces business rules BR-159-012 (Subscription Limits) and Rate Limiting.
/// Blocks incoming gRPC calls to join/create a room if the workspace is out of quota.
/// </summary>
public class SubscriptionQuotaInterceptor : Interceptor
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<SubscriptionQuotaInterceptor> _logger;

    public SubscriptionQuotaInterceptor(IConnectionMultiplexer redis, ILogger<SubscriptionQuotaInterceptor> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        // Example: Only intercept JoinRoom or CreateRoom methods
        if (context.Method.EndsWith("/JoinRoom") || context.Method.EndsWith("/CreateRoom"))
        {
            var workspaceId = ExtractWorkspaceId(request);

            if (!string.IsNullOrEmpty(workspaceId))
            {
                var isQuotaExceeded = await CheckQuotaFromRedisAsync(workspaceId);

                if (isQuotaExceeded)
                {
                    _logger.LogWarning("Quota exceeded for Workspace {WorkspaceId}. Blocking {Method}", workspaceId, context.Method);
                    throw new RpcException(new Status(StatusCode.ResourceExhausted, "Workspace has exceeded its active meeting quota."));
                }
            }
        }

        return await continuation(request, context);
    }

    private string? ExtractWorkspaceId<TRequest>(TRequest request)
    {
        // In a real scenario, use Reflection or dynamic casting to extract WorkspaceId from the gRPC request object
        var property = typeof(TRequest).GetProperty("WorkspaceId");
        return property?.GetValue(request)?.ToString();
    }

    private async Task<bool> CheckQuotaFromRedisAsync(string workspaceId)
    {
        var db = _redis.GetDatabase();
        // Assume BillingService updates "workspace:{id}:quota:exceeded"
        var value = await db.StringGetAsync($"workspace:{workspaceId}:quota:exceeded");
        return value.HasValue && value == "true";
    }
}

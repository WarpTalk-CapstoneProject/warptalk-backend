using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Threading.Tasks;

namespace WarpTalk.TranslationRoomService.API.Interceptors;

/// <summary>
/// Enforces business rules BR-159-012 (Subscription Limits) and Rate Limiting.
/// Blocks incoming gRPC calls to join/create a room if the workspace is out of quota.
/// </summary>
public class SubscriptionQuotaInterceptor : Interceptor
{
    private const string WorkspaceQuotaExceededKeyTemplate = "workspace:{0}:quota:exceeded";
    private const string WorkspaceAiServiceSuspendedKeyTemplate = "workspace:{0}:ai_service_suspended";
    private const string RedisTrueValue = "true";
    private const string AiServiceSuspendedMessage = "Workspace AI service is suspended.";
    private const string MeetingQuotaExceededMessage = "Workspace has exceeded its active meeting quota.";

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
                var isAiServiceSuspended = await CheckAiServiceSuspendedFromRedisAsync(workspaceId);

                if (isQuotaExceeded || isAiServiceSuspended)
                {
                    var reason = isAiServiceSuspended
                        ? AiServiceSuspendedMessage
                        : MeetingQuotaExceededMessage;

                    _logger.LogWarning(
                        "Blocking {Method} for Workspace {WorkspaceId}. QuotaExceeded={QuotaExceeded}, AiServiceSuspended={AiServiceSuspended}",
                        context.Method,
                        workspaceId,
                        isQuotaExceeded,
                        isAiServiceSuspended);
                    throw new RpcException(new Status(StatusCode.ResourceExhausted, reason));
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
        var value = await db.StringGetAsync(string.Format(WorkspaceQuotaExceededKeyTemplate, workspaceId));
        return value.HasValue && value == RedisTrueValue;
    }

    private async Task<bool> CheckAiServiceSuspendedFromRedisAsync(string workspaceId)
    {
        var db = _redis.GetDatabase();
        var value = await db.StringGetAsync(string.Format(WorkspaceAiServiceSuspendedKeyTemplate, workspaceId));
        return value.HasValue && value == RedisTrueValue;
    }
}

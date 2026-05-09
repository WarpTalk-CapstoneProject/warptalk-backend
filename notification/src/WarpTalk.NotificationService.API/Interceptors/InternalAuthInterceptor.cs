using Grpc.Core;
using Grpc.Core.Interceptors;

namespace WarpTalk.NotificationService.API.Interceptors;

/// <summary>
/// gRPC Interceptor for Zero-Trust Inter-service Authentication.
/// Secures internal communication between microservices by validating an internal secret token.
/// </summary>
public class InternalAuthInterceptor : Interceptor
{
    private readonly string _internalSecret;
    private readonly ILogger<InternalAuthInterceptor> _logger;

    public InternalAuthInterceptor(IConfiguration configuration, ILogger<InternalAuthInterceptor> logger, IWebHostEnvironment env)
    {
        var rawGrpcSecret = configuration["Grpc:InternalSecret"];
        var isDefaultOrInvalid = string.IsNullOrWhiteSpace(rawGrpcSecret) || 
                                 rawGrpcSecret.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
                                 rawGrpcSecret.Length < 32;

        if (env.IsProduction() && isDefaultOrInvalid)
        {
            throw new InvalidOperationException("CRITICAL SECURITY: Grpc Internal Secret is not properly configured for Production. It must be at least 32 characters long and not be the default placeholder.");
        }

        _internalSecret = isDefaultOrInvalid 
            ? "CHANGE_ME_INTERNAL_SECRET_MIN_32_CHARS_LONG!!" 
            : rawGrpcSecret!;

        _logger = logger;
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, 
        ServerCallContext context, 
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var token = context.RequestHeaders.GetValue("x-internal-token");

        if (string.IsNullOrEmpty(token) || !string.Equals(token, _internalSecret, StringComparison.Ordinal))
        {
            _logger.LogWarning("gRPC Zero-Trust Authentication failed for endpoint {Method}. Token is missing or invalid.", context.Method);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid or missing internal token"));
        }

        return continuation(request, context);
    }
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.ClientFactory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WarpTalk.Shared.Grpc;

public static class InternalGrpcSecurity
{
    public const string HeaderName = "x-internal-token";

    public static IServiceCollection AddWarpTalkGrpcServer(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ValidateSecret(configuration, environment);
        services.AddGrpc(options =>
            options.Interceptors.Add<InternalGrpcServerAuthInterceptor>());
        return services;
    }

    public static IHttpClientBuilder AddWarpTalkGrpcClientDefaults(
        this IHttpClientBuilder builder,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ValidateSecret(configuration, environment);
        builder.Services.TryAddTransient<InternalGrpcClientInterceptor>();

        builder.ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = new HttpClientHandler();
            if (environment.IsDevelopment())
            {
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }

            return handler;
        });

        builder.AddInterceptor<InternalGrpcClientInterceptor>(InterceptorScope.Client);
        return builder;
    }

    internal static string GetValidatedSecret(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var secret = configuration["Grpc:InternalSecret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "Grpc:InternalSecret is required; no implicit fallback is allowed.");
        }

        var invalidForProduction =
            secret.Length < 32
            || secret.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
            || secret.Contains("placeholder", StringComparison.OrdinalIgnoreCase);
        if (invalidForProduction && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Grpc:InternalSecret must contain at least 32 characters and must not be a placeholder outside Development.");
        }

        return secret;
    }

    private static void ValidateSecret(
        IConfiguration configuration,
        IHostEnvironment environment) =>
        _ = GetValidatedSecret(configuration, environment);
}

public sealed class InternalGrpcServerAuthInterceptor(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<InternalGrpcServerAuthInterceptor> logger) : Interceptor
{
    private readonly string _secret =
        InternalGrpcSecurity.GetValidatedSecret(configuration, environment);

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(request, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(requestStream, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(request, responseStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(requestStream, responseStream, context);
    }

    private void Authorize(ServerCallContext context)
    {
        var supplied = context.RequestHeaders.GetValue(InternalGrpcSecurity.HeaderName);
        if (CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(supplied ?? string.Empty),
                Encoding.UTF8.GetBytes(_secret)))
        {
            return;
        }

        logger.LogWarning(
            "Internal gRPC authentication rejected method {Method}.",
            context.Method);
        throw new RpcException(
            new Status(StatusCode.Unauthenticated, "Invalid or missing internal token."));
    }
}

public sealed class InternalGrpcClientInterceptor(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<InternalGrpcClientInterceptor> logger) : Interceptor
{
    private static readonly ConcurrentDictionary<string, CircuitState> Circuits = new();
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CircuitDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);
    private const int FailureThreshold = 5;

    private readonly string _secret =
        InternalGrpcSecurity.GetValidatedSecret(configuration, environment);

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var state = Circuits.GetOrAdd(context.Method.FullName, _ => new CircuitState());
        if (state.IsOpen(DateTimeOffset.UtcNow))
        {
            throw new RpcException(
                new Status(
                    StatusCode.Unavailable,
                    $"Circuit is open for {context.Method.FullName}."));
        }

        var call = continuation(request, WithSecurityDefaults(context));
        return new AsyncUnaryCall<TResponse>(
            ObserveResponseAsync(call.ResponseAsync, state, context.Method.FullName),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, WithSecurityHeaders(context));

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation) =>
        continuation(WithSecurityHeaders(context));

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation) =>
        continuation(WithSecurityHeaders(context));

    private ClientInterceptorContext<TRequest, TResponse> WithSecurityDefaults<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        var securedContext = WithSecurityHeaders(context);
        var deadline = context.Options.Deadline
                       ?? DateTime.UtcNow.Add(DefaultDeadline);
        var options = securedContext.Options.WithDeadline(deadline);
        return new ClientInterceptorContext<TRequest, TResponse>(
            context.Method,
            context.Host,
            options);
    }

    private ClientInterceptorContext<TRequest, TResponse> WithSecurityHeaders<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        var headers = context.Options.Headers is null
            ? new Metadata()
            : CloneMetadata(context.Options.Headers);
        if (headers.Get(InternalGrpcSecurity.HeaderName) is null)
        {
            headers.Add(InternalGrpcSecurity.HeaderName, _secret);
        }

        return new ClientInterceptorContext<TRequest, TResponse>(
            context.Method,
            context.Host,
            context.Options.WithHeaders(headers));
    }

    private async Task<TResponse> ObserveResponseAsync<TResponse>(
        Task<TResponse> response,
        CircuitState state,
        string method)
    {
        try
        {
            var result = await response.ConfigureAwait(false);
            state.RecordSuccess();
            return result;
        }
        catch (RpcException exception) when (
            exception.StatusCode is StatusCode.Unavailable
                or StatusCode.ResourceExhausted
                or StatusCode.DeadlineExceeded)
        {
            if (state.RecordFailure(
                    DateTimeOffset.UtcNow,
                    FailureThreshold,
                    FailureWindow,
                    CircuitDuration))
            {
                logger.LogWarning(
                    "Opened internal gRPC circuit for {Method} for {DurationSeconds} seconds.",
                    method,
                    CircuitDuration.TotalSeconds);
            }

            throw;
        }
    }

    private static Metadata CloneMetadata(Metadata source)
    {
        var clone = new Metadata();
        foreach (var entry in source)
        {
            if (entry.IsBinary)
            {
                clone.Add(entry.Key, entry.ValueBytes);
            }
            else
            {
                clone.Add(entry.Key, entry.Value);
            }
        }

        return clone;
    }

    private sealed class CircuitState
    {
        private readonly object _gate = new();
        private int _failures;
        private DateTimeOffset _failureWindowStartedAt;
        private DateTimeOffset _openUntil;

        public bool IsOpen(DateTimeOffset now)
        {
            lock (_gate)
            {
                return _openUntil > now;
            }
        }

        public bool RecordFailure(
            DateTimeOffset now,
            int threshold,
            TimeSpan failureWindow,
            TimeSpan duration)
        {
            lock (_gate)
            {
                if (
                    _failureWindowStartedAt == default ||
                    now - _failureWindowStartedAt > failureWindow)
                {
                    _failureWindowStartedAt = now;
                    _failures = 0;
                }

                _failures++;
                if (_failures < threshold)
                {
                    return false;
                }

                _failures = 0;
                _failureWindowStartedAt = default;
                _openUntil = now.Add(duration);
                return true;
            }
        }

        public void RecordSuccess()
        {
            lock (_gate)
            {
                _failures = 0;
                _failureWindowStartedAt = default;
                _openUntil = default;
            }
        }
    }
}

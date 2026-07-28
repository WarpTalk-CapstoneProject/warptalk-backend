using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared.Grpc;

namespace WarpTalk.NotificationService.Tests;

public sealed class InternalGrpcServerAuthInterceptorTests
{
    private const string Secret = "test-only-internal-grpc-secret-32-characters";

    [Fact]
    public async Task UnaryCall_WithMatchingToken_ReachesHandler()
    {
        var interceptor = CreateInterceptor();
        var context = CreateContext(Secret);

        var response = await interceptor.UnaryServerHandler(
            "request",
            context,
            (_, _) => Task.FromResult("response"));

        Assert.Equal("response", response);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-token")]
    public async Task UnaryCall_WithoutMatchingToken_IsRejected(string? token)
    {
        var interceptor = CreateInterceptor();
        var context = CreateContext(token);

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            interceptor.UnaryServerHandler(
                "request",
                context,
                (_, _) => Task.FromResult("response")));

        Assert.Equal(StatusCode.Unauthenticated, exception.StatusCode);
    }

    private static InternalGrpcServerAuthInterceptor CreateInterceptor()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Grpc:InternalSecret"] = Secret
            })
            .Build();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns(Environments.Development);

        return new InternalGrpcServerAuthInterceptor(
            configuration,
            environment.Object,
            NullLogger<InternalGrpcServerAuthInterceptor>.Instance);
    }

    private static ServerCallContext CreateContext(string? token)
    {
        var headers = new Metadata();
        if (token is not null)
        {
            headers.Add(InternalGrpcSecurity.HeaderName, token);
        }

        return new TestServerCallContext(headers);
    }

    private sealed class TestServerCallContext(Metadata requestHeaders) : ServerCallContext
    {
        private readonly Metadata _responseTrailers = [];
        private readonly Dictionary<object, object> _userState = [];
        private Status _status = Status.DefaultSuccess;
        private WriteOptions? _writeOptions;

        protected override string MethodCore => "/warptalk.test/Unary";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "ipv4:127.0.0.1:0";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => requestHeaders;
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => _responseTrailers;

        protected override Status StatusCore
        {
            get => _status;
            set => _status = value;
        }

        protected override WriteOptions? WriteOptionsCore
        {
            get => _writeOptions;
            set => _writeOptions = value;
        }

        protected override AuthContext AuthContextCore =>
            new("anonymous", new Dictionary<string, List<AuthProperty>>());

        protected override IDictionary<object, object> UserStateCore => _userState;

        protected override ContextPropagationToken CreatePropagationTokenCore(
            ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
            Task.CompletedTask;
    }
}

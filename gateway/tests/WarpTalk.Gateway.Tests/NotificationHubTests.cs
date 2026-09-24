using System.Security.Claims;
using Grpc.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Presence;
using WarpTalk.Shared.Protos;

namespace WarpTalk.Gateway.Tests;

public class NotificationHubTests
{
    private readonly Mock<IConnectionManager> _mockConnectionManager;
    private readonly Mock<IPresenceNotifier> _mockPresenceNotifier;
    private readonly Mock<IPresenceQueryService> _mockPresenceQuery;
    private readonly Dictionary<object, object?> _connectionItems = new();
    private readonly Mock<ILogger<NotificationHub>> _mockLogger;
    private readonly Mock<NotificationGrpcService.NotificationGrpcServiceClient> _mockGrpcClient;
    private readonly Mock<IHubCallerClients> _mockClients;
    private readonly Mock<IClientProxy> _mockClientProxy;
    private readonly Mock<HubCallerContext> _mockContext;
    private readonly Mock<ISingleClientProxy> _mockSingleClientProxy;
    private readonly NotificationHub _hub;

    private readonly Guid _userId = Guid.NewGuid();
    private readonly string _connectionId = "test-connection-id";

    public NotificationHubTests()
    {
        _mockConnectionManager = new Mock<IConnectionManager>();
        _mockPresenceNotifier = new Mock<IPresenceNotifier>();
        _mockPresenceQuery = new Mock<IPresenceQueryService>();
        _mockLogger = new Mock<ILogger<NotificationHub>>();

        // Mock gRPC Client
        var mockCallInvoker = new Mock<CallInvoker>();
        _mockGrpcClient = new Mock<NotificationGrpcService.NotificationGrpcServiceClient>(mockCallInvoker.Object);

        _mockClients = new Mock<IHubCallerClients>();
        _mockClientProxy = new Mock<IClientProxy>();
        _mockSingleClientProxy = new Mock<ISingleClientProxy>();
        _mockContext = new Mock<HubCallerContext>();

        // Setup Hub Caller Context
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, _userId.ToString()) };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new ClaimsPrincipal(identity);

        _mockContext.Setup(c => c.User).Returns(claimsPrincipal);
        _mockContext.Setup(c => c.ConnectionId).Returns(_connectionId);
        _mockContext.Setup(c => c.Items).Returns(_connectionItems);

        // Setup Clients
        _mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockClientProxy.Object);
        _mockClients.Setup(c => c.Caller).Returns(_mockSingleClientProxy.Object);

        _hub = new NotificationHub(
            _mockConnectionManager.Object,
            _mockPresenceNotifier.Object,
            _mockPresenceQuery.Object,
            _mockLogger.Object,
            _mockGrpcClient.Object)
        {
            Context = _mockContext.Object,
            Clients = _mockClients.Object,
            Groups = new Mock<IGroupManager>().Object
        };
    }

    [Fact]
    public async Task MarkAsRead_Success_BroadcastsToUserGroup()
    {
        // Arrange
        var notificationId = Guid.NewGuid();
        var response = new MarkAsReadResponse { Success = true };

        _mockGrpcClient
            .Setup(c => c.MarkAsReadAsync(
                It.Is<MarkAsReadRequest>(r => r.UserId == _userId.ToString() && r.NotificationId == notificationId.ToString()),
                null, null, default))
            .Returns(new Grpc.Core.AsyncUnaryCall<MarkAsReadResponse>(
                Task.FromResult(response),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        // Act
        await _hub.MarkAsRead(notificationId);

        // Assert
        _mockClients.Verify(c => c.Group($"user:{_userId}"), Times.Once);
        _mockClientProxy.Verify(
            p => p.SendCoreAsync("NotificationRead", new object[] { notificationId }, default),
            Times.Once);
    }

    [Fact]
    public async Task MarkAsRead_GrpcFails_SendsErrorToCaller()
    {
        // Arrange
        var notificationId = Guid.NewGuid();
        var response = new MarkAsReadResponse { Success = false, ErrorMessage = "Not Found" };

        _mockGrpcClient
            .Setup(c => c.MarkAsReadAsync(It.IsAny<MarkAsReadRequest>(), null, null, default))
            .Returns(new Grpc.Core.AsyncUnaryCall<MarkAsReadResponse>(
                Task.FromResult(response),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        // Act
        await _hub.MarkAsRead(notificationId);

        // Assert
        _mockClients.Verify(c => c.Caller, Times.Once);
        _mockSingleClientProxy.Verify(
            p => p.SendCoreAsync("NotificationError", new object[] { "Failed to mark as read: Not Found" }, default),
            Times.Once);
    }

    [Fact]
    public async Task MarkAsRead_GrpcThrowsException_CaughtAndSendsErrorToCaller()
    {
        // Arrange
        var notificationId = Guid.NewGuid();

        _mockGrpcClient
            .Setup(c => c.MarkAsReadAsync(It.IsAny<MarkAsReadRequest>(), null, null, default))
            .Throws(new RpcException(new Status(StatusCode.Unavailable, "Service down")));

        // Act
        await _hub.MarkAsRead(notificationId);

        // Assert
        _mockClients.Verify(c => c.Caller, Times.Once);
        _mockSingleClientProxy.Verify(
            p => p.SendCoreAsync("NotificationError", new object[] { "An error occurred while marking as read." }, default),
            Times.Once);
    }

    [Fact]
    public async Task MarkAllAsRead_Success_BroadcastsToUserGroup()
    {
        // Arrange
        var response = new MarkAllAsReadResponse { Success = true };

        _mockGrpcClient
            .Setup(c => c.MarkAllAsReadAsync(
                It.Is<MarkAllAsReadRequest>(r => r.UserId == _userId.ToString()),
                null, null, default))
            .Returns(new Grpc.Core.AsyncUnaryCall<MarkAllAsReadResponse>(
                Task.FromResult(response),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        // Act
        await _hub.MarkAllAsRead();

        // Assert
        _mockClients.Verify(c => c.Group($"user:{_userId}"), Times.Once);
        _mockClientProxy.Verify(
            p => p.SendCoreAsync("AllNotificationsRead", Array.Empty<object>(), default),
            Times.Once);
    }

    [Fact]
    public async Task MarkAllAsRead_GrpcThrowsException_CaughtAndSendsErrorToCaller()
    {
        // Arrange
        _mockGrpcClient
            .Setup(c => c.MarkAllAsReadAsync(It.IsAny<MarkAllAsReadRequest>(), null, null, default))
            .Throws(new RpcException(new Status(StatusCode.Unavailable, "Service down")));

        // Act
        await _hub.MarkAllAsRead();

        // Assert
        _mockClients.Verify(c => c.Caller, Times.Once);
        _mockSingleClientProxy.Verify(
            p => p.SendCoreAsync("NotificationError", new object[] { "An error occurred while marking all as read." }, default),
            Times.Once);
    }

    // ── QueryPresence: the presence snapshot over the hub ──────────────────

    [Fact]
    public async Task QueryPresence_AnswersFromTheSharedPresenceQuery_AsTheCaller()
    {
        var ids = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };
        var expected = new PresenceQueryResponse(new Dictionary<string, string>
        {
            [ids[0]] = "Online",
            [ids[1]] = "Offline",
        });
        _mockPresenceQuery
            .Setup(q => q.QueryAsync(_userId.ToString(), ids, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _hub.QueryPresence(ids);

        // The caller identity comes from the connection's token, never from the arguments: the
        // WT-335 workspace filter is only as good as the id it filters for.
        Assert.Same(expected, result);
        _mockPresenceQuery.Verify(
            q => q.QueryAsync(_userId.ToString(), ids, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task QueryPresence_RepliesOnlyToTheCaller_AndNeverReadsReplicaLocalState()
    {
        _mockPresenceQuery
            .Setup(q => q.QueryAsync(It.IsAny<string?>(), It.IsAny<IEnumerable<string?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PresenceQueryResponse(new Dictionary<string, string>()));

        await _hub.QueryPresence([Guid.NewGuid().ToString()]);

        // Multi-replica safety: the snapshot is the invocation's return value, so it goes back on
        // the caller's own socket — no group send that would need the backplane or a relay leader,
        // and no read of the in-memory connection table, which only knows this pod's half.
        _mockClients.VerifyNoOtherCalls();
        _mockConnectionManager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task QueryPresence_IsThrottledPerConnection_BecauseHubCallsBypassTheHttpLimiter()
    {
        _mockPresenceQuery
            .Setup(q => q.QueryAsync(It.IsAny<string?>(), It.IsAny<IEnumerable<string?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PresenceQueryResponse(new Dictionary<string, string>()));

        for (var i = 0; i < PresenceQueryThrottle.MaxCallsPerWindow; i++)
        {
            await _hub.QueryPresence(["a"]);
        }

        var refused = await Assert.ThrowsAsync<HubException>(() => _hub.QueryPresence(["a"]));
        Assert.Contains("rate limit", refused.Message, StringComparison.OrdinalIgnoreCase);

        // The refused call never reached WorkspaceService or Redis.
        _mockPresenceQuery.Verify(
            q => q.QueryAsync(It.IsAny<string?>(), It.IsAny<IEnumerable<string?>?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(PresenceQueryThrottle.MaxCallsPerWindow));
    }

    [Fact]
    public async Task QueryPresence_ThrottleBelongsToTheConnection_NotTheUser()
    {
        _mockPresenceQuery
            .Setup(q => q.QueryAsync(It.IsAny<string?>(), It.IsAny<IEnumerable<string?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PresenceQueryResponse(new Dictionary<string, string>()));

        for (var i = 0; i < PresenceQueryThrottle.MaxCallsPerWindow; i++)
        {
            await _hub.QueryPresence(["a"]);
        }

        // A second tab of the same user is a second connection with its own Items.
        _connectionItems.Clear();

        await _hub.QueryPresence(["a"]);
    }
}

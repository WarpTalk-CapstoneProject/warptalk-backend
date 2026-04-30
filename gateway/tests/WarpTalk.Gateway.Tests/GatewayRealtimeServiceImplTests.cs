using Grpc.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.Gateway.GrpcServices;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Shared.Protos;

namespace WarpTalk.Gateway.Tests;

public class GatewayRealtimeServiceImplTests
{
    private readonly Mock<IHubContext<NotificationHub>> _mockHubContext;
    private readonly Mock<IHubClients> _mockClients;
    private readonly Mock<IClientProxy> _mockClientProxy;
    private readonly Mock<ILogger<GatewayRealtimeServiceImpl>> _mockLogger;
    private readonly GatewayRealtimeServiceImpl _service;

    public GatewayRealtimeServiceImplTests()
    {
        _mockHubContext = new Mock<IHubContext<NotificationHub>>();
        _mockClients = new Mock<IHubClients>();
        _mockClientProxy = new Mock<IClientProxy>();
        _mockLogger = new Mock<ILogger<GatewayRealtimeServiceImpl>>();

        _mockHubContext.Setup(c => c.Clients).Returns(_mockClients.Object);
        _mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockClientProxy.Object);

        _service = new GatewayRealtimeServiceImpl(_mockHubContext.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task PushNewNotification_WithValidRequest_BroadcastsToUserGroupAndReturnsSuccess()
    {
        // Arrange
        var request = new PushNewNotificationRequest
        {
            Id = Guid.NewGuid().ToString(),
            UserId = Guid.NewGuid().ToString(),
            Title = "Test Title",
            Content = "Test Content",
            Type = "SYSTEM",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };
        var mockCallContext = new Mock<ServerCallContext>().Object;

        // Act
        var response = await _service.PushNewNotification(request, mockCallContext);

        // Assert
        Assert.True(response.Success);
        _mockClients.Verify(c => c.Group($"user:{request.UserId}"), Times.Once);
        _mockClientProxy.Verify(
            p => p.SendCoreAsync("NewNotification", new object[] { request }, default),
            Times.Once);
    }

    [Fact]
    public async Task PushNewNotification_EmptyUserId_ReturnsFailureWithoutBroadcasting()
    {
        // Arrange
        var request = new PushNewNotificationRequest
        {
            Id = Guid.NewGuid().ToString(),
            UserId = "", // Empty
            Title = "Test Title"
        };
        var mockCallContext = new Mock<ServerCallContext>().Object;

        // Act
        var response = await _service.PushNewNotification(request, mockCallContext);

        // Assert
        Assert.False(response.Success);
        _mockClients.Verify(c => c.Group(It.IsAny<string>()), Times.Never);
        _mockClientProxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), default), Times.Never);
    }

    [Fact]
    public async Task PushNewNotification_ExceptionThrown_ReturnsFailure()
    {
        // Arrange
        var request = new PushNewNotificationRequest
        {
            Id = Guid.NewGuid().ToString(),
            UserId = Guid.NewGuid().ToString(),
            Title = "Test Title"
        };
        var mockCallContext = new Mock<ServerCallContext>().Object;

        // Force exception
        _mockClients.Setup(c => c.Group(It.IsAny<string>())).Throws(new Exception("SignalR disconnected"));

        // Act
        var response = await _service.PushNewNotification(request, mockCallContext);

        // Assert
        Assert.False(response.Success);
    }
}

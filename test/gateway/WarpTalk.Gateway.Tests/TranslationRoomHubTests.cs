using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using System;
using System.Security.Claims;
using System.Threading.Tasks;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Services;
using Xunit;

namespace WarpTalk.Gateway.Tests;

public class TranslationRoomHubTests
{
    [Fact]
    public async Task JoinTranslationRoom_ShouldKickOldConnection_WhenUserJoinsFromNewDevice()
    {
        // Arrange
        var connectionManagerMock = new Mock<IConnectionManager>();
        
        var redisMock = new Mock<IConnectionMultiplexer>();
        var dbMock = new Mock<IDatabase>();
        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(dbMock.Object);

        var configMock = new Mock<Microsoft.Extensions.Configuration.IConfiguration>();
        var configSectionMock = new Mock<Microsoft.Extensions.Configuration.IConfigurationSection>();
        configSectionMock.Setup(s => s.Value).Returns("10000");
        configMock.Setup(c => c.GetSection(It.IsAny<string>())).Returns(configSectionMock.Object);
        
        var streamService = new RedisStreamService(redisMock.Object, new NullLogger<RedisStreamService>(), configMock.Object);
        var translationRoomRegistry = new ActiveTranslationRoomRegistry();

        var hub = new TranslationRoomHub(
            connectionManagerMock.Object,
            streamService,
            translationRoomRegistry,
            redisMock.Object,
            new NullLogger<TranslationRoomHub>()
        );

        // Mock HubContext Context (Claims & ConnectionId)
        var userId = Guid.NewGuid().ToString();
        var roomId = Guid.NewGuid();
        
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId) };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        var claimsPrincipal = new ClaimsPrincipal(identity);

        var oldConnectionId = "old-conn-1";
        var newConnectionId = "new-conn-2";

        var oldHubCallerContextMock = new Mock<HubCallerContext>();
        oldHubCallerContextMock.Setup(c => c.User).Returns(claimsPrincipal);
        oldHubCallerContextMock.Setup(c => c.ConnectionId).Returns(oldConnectionId);

        var newHubCallerContextMock = new Mock<HubCallerContext>();
        newHubCallerContextMock.Setup(c => c.User).Returns(claimsPrincipal);
        newHubCallerContextMock.Setup(c => c.ConnectionId).Returns(newConnectionId);

        // Mock Clients
        var mockClients = new Mock<IHubCallerClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        var mockSingleClientProxy = new Mock<ISingleClientProxy>();
        
        mockClients.Setup(c => c.Client(It.IsAny<string>())).Returns(mockSingleClientProxy.Object);
        mockClients.Setup(c => c.OthersInGroup(It.IsAny<string>())).Returns(mockClientProxy.Object);
        hub.Clients = mockClients.Object;

        // Mock Groups
        var mockGroups = new Mock<IGroupManager>();
        hub.Groups = mockGroups.Object;

        // Act - First Join (Old device)
        hub.Context = oldHubCallerContextMock.Object;
        await hub.JoinTranslationRoom(roomId, "User1", "en", "vi");

        // Act - Second Join (New device)
        hub.Context = newHubCallerContextMock.Object;
        await hub.JoinTranslationRoom(roomId, "User1", "en", "vi");

        // Assert
        // Verify that the old connection was sent the "ForceDisconnected" message
        mockClients.Verify(c => c.Client(oldConnectionId), Times.Once);
        mockSingleClientProxy.Verify(
            p => p.SendCoreAsync("ForceDisconnected", It.IsAny<object[]>(), default), 
            Times.Once);

        // Verify that the old connection was removed from the SignalR Group
        mockGroups.Verify(g => g.RemoveFromGroupAsync(oldConnectionId, $"translationRoom:{roomId}", default), Times.Once);
    }
}

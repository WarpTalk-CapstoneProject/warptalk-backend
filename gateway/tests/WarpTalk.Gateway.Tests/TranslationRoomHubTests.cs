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

    [Fact]
    public async Task JoinTranslationRoom_ShouldStoreNormalizedListenLanguage_ForAiPipelineRouting()
    {
        var (hub, dbMock, _, _, _, _) = CreateHub();
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid().ToString();
        hub.Context = CreateContext(userId, "conn-join");

        await hub.JoinTranslationRoom(roomId, "User1", "en-US", "vi-VN");

        dbMock.Verify(
            db => db.HashSetAsync(
                $"translationRoom:{roomId}:languages",
                userId,
                "vi",
                When.Always,
                CommandFlags.None),
            Times.Once);
    }

    [Fact]
    public async Task SetListenLanguage_ShouldStoreAndBroadcastNormalizedLanguage_ForImmediateSwitch()
    {
        var (hub, dbMock, clientsMock, clientProxyMock, _, _) = CreateHub();
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid().ToString();
        hub.Context = CreateContext(userId, "conn-switch");

        await hub.SetListenLanguage(roomId, "en-US");

        dbMock.Verify(
            db => db.HashSetAsync(
                $"translationRoom:{roomId}:languages",
                userId,
                "en",
                When.Always,
                CommandFlags.None),
            Times.Once);
        clientsMock.Verify(c => c.OthersInGroup($"translationRoom:{roomId}"), Times.Once);
        clientProxyMock.Verify(
            p => p.SendCoreAsync(
                "ParticipantLanguageChanged",
                It.Is<object[]>(args => (string)args[0] == userId && (string)args[1] == "en"),
                default),
            Times.Once);
    }

    [Fact]
    public async Task SetSpeakLanguage_ShouldStoreAndBroadcastNormalizedLanguage_ForImmediateSwitch()
    {
        var (hub, dbMock, clientsMock, clientProxyMock, _, _) = CreateHub();
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid().ToString();
        hub.Context = CreateContext(userId, "conn-speak-switch");

        await hub.SetSpeakLanguage(roomId, "vi-VN");

        dbMock.Verify(
            db => db.HashSetAsync(
                $"translationRoom:{roomId}:speak_languages",
                userId,
                "vi",
                When.Always,
                CommandFlags.None),
            Times.Once);
        clientsMock.Verify(c => c.OthersInGroup($"translationRoom:{roomId}"), Times.Once);
        clientProxyMock.Verify(
            p => p.SendCoreAsync(
                "ParticipantSpeakLanguageChanged",
                It.Is<object[]>(args => (string)args[0] == userId && (string)args[1] == "vi"),
                default),
            Times.Once);
    }

    [Fact]
    public async Task SetSpeakLanguage_ShouldThrow_WhenLanguageIsMissing()
    {
        var (hub, _, _, _, _, _) = CreateHub();
        hub.Context = CreateContext(Guid.NewGuid().ToString(), "conn-speak-missing");

        await Assert.ThrowsAsync<HubException>(() => hub.SetSpeakLanguage(Guid.NewGuid(), " "));
    }

    [Fact]
    public async Task SetVoicePreference_ShouldStoreVoiceIdAndBroadcast_WhenNonEmpty()
    {
        var (hub, dbMock, clientsMock, clientProxyMock, _, _) = CreateHub();
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid().ToString();
        hub.Context = CreateContext(userId, "conn-voice");

        await hub.SetVoicePreference(roomId, "voice-abc-123");

        dbMock.Verify(
            db => db.HashSetAsync(
                $"translationRoom:{roomId}:voice_preferences",
                userId,
                "voice-abc-123",
                When.Always,
                CommandFlags.None),
            Times.Once);
        clientsMock.Verify(c => c.OthersInGroup($"translationRoom:{roomId}"), Times.Once);
        clientProxyMock.Verify(
            p => p.SendCoreAsync(
                "ParticipantVoiceChanged",
                It.Is<object[]>(args => (string)args[0] == userId && (string)args[1] == "voice-abc-123"),
                default),
            Times.Once);
    }

    [Fact]
    public async Task SetVoicePreference_ShouldDeleteHashField_WhenClearedWithEmptyString()
    {
        var (hub, dbMock, _, _, _, _) = CreateHub();
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid().ToString();
        hub.Context = CreateContext(userId, "conn-voice-clear");

        await hub.SetVoicePreference(roomId, "");

        dbMock.Verify(
            db => db.HashDeleteAsync(
                $"translationRoom:{roomId}:voice_preferences",
                userId,
                CommandFlags.None),
            Times.Once);
        dbMock.Verify(
            db => db.HashSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()),
            Times.Never);
    }

    [Fact]
    public async Task GetVoiceCatalog_ShouldReturnParsedEntries_WhenCachePresent()
    {
        var (hub, dbMock, _, _, _, _) = CreateHub();
        hub.Context = CreateContext(Guid.NewGuid().ToString(), "conn-catalog");
        const string json = "[{\"id\":\"v1\",\"name\":\"Voice One\",\"gender\":\"female\"}]";
        dbMock.Setup(db => db.StringGetAsync("voice_catalog:vi", CommandFlags.None))
            .ReturnsAsync((RedisValue)json);

        var result = await hub.GetVoiceCatalog("vi-VN");

        var voice = Assert.Single(result);
        Assert.Equal("v1", voice.Id);
        Assert.Equal("Voice One", voice.Name);
        Assert.Equal("female", voice.Gender);
    }

    [Fact]
    public async Task GetVoiceCatalog_ShouldReturnEmptyList_WhenCacheMissing()
    {
        var (hub, dbMock, _, _, _, _) = CreateHub();
        hub.Context = CreateContext(Guid.NewGuid().ToString(), "conn-catalog-empty");
        dbMock.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        var result = await hub.GetVoiceCatalog("vi");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetVoiceCatalog_ShouldReturnEmptyList_WhenLanguageBlank()
    {
        var (hub, _, _, _, _, _) = CreateHub();
        hub.Context = CreateContext(Guid.NewGuid().ToString(), "conn-catalog-blank");

        var result = await hub.GetVoiceCatalog("   ");

        Assert.Empty(result);
    }

    [Fact]
    public async Task RaiseHand_ShouldBroadcastToOthersInGroup()
    {
        var (hub, _, clientsMock, clientProxyMock, _, _) = CreateHub();
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid().ToString();
        hub.Context = CreateContext(userId, "conn-raise-hand");

        await hub.RaiseHand(roomId, true);

        clientsMock.Verify(c => c.OthersInGroup($"translationRoom:{roomId}"), Times.Once);
        clientProxyMock.Verify(
            p => p.SendCoreAsync(
                "HandRaised",
                It.Is<object[]>(args => (string)args[0] == userId && (bool)args[1] == true),
                default),
            Times.Once);
    }

    [Fact]
    public async Task LeaveTranslationRoom_ShouldBroadcastHandRaisedFalse_SoAStuckHandIsNeverLeftBehind()
    {
        var (hub, _, clientsMock, clientProxyMock, _, _) = CreateHub();
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid().ToString();
        hub.Context = CreateContext(userId, "conn-leave-hand");

        await hub.LeaveTranslationRoom(roomId);

        clientProxyMock.Verify(
            p => p.SendCoreAsync(
                "HandRaised",
                It.Is<object[]>(args => (string)args[0] == userId && (bool)args[1] == false),
                default),
            Times.Once);
    }

    [Fact]
    public async Task SendReaction_ShouldBroadcastToWholeGroup_IncludingSender_WhenEmojiAllowed()
    {
        var (hub, _, clientsMock, _, _, groupClientProxyMock) = CreateHub();
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid().ToString();
        hub.Context = CreateContext(userId, "conn-reaction");

        await hub.SendReaction(roomId, "🎉");

        clientsMock.Verify(c => c.Group($"translationRoom:{roomId}"), Times.Once);
        groupClientProxyMock.Verify(
            p => p.SendCoreAsync(
                "ReactionReceived",
                It.Is<object[]>(args => (string)args[0] == userId && (string)args[1] == "🎉" && args[2] is DateTime),
                default),
            Times.Once);
    }

    [Fact]
    public async Task SendReaction_ShouldThrowHubException_WhenEmojiNotOnAllowList()
    {
        var (hub, _, _, _, _, _) = CreateHub();
        hub.Context = CreateContext(Guid.NewGuid().ToString(), "conn-reaction-invalid");

        await Assert.ThrowsAsync<HubException>(() => hub.SendReaction(Guid.NewGuid(), "🔥"));
    }

    [Fact]
    public async Task SpotlightParticipant_ShouldBroadcastToWholeGroup()
    {
        var (hub, _, clientsMock, _, _, groupClientProxyMock) = CreateHub();
        var roomId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        hub.Context = CreateContext(Guid.NewGuid().ToString(), "conn-spotlight");

        await hub.SpotlightParticipant(roomId, targetUserId, true);

        clientsMock.Verify(c => c.Group($"translationRoom:{roomId}"), Times.Once);
        groupClientProxyMock.Verify(
            p => p.SendCoreAsync(
                "SpotlightChanged",
                It.Is<object[]>(args => (Guid)args[0] == targetUserId && (bool)args[1] == true),
                default),
            Times.Once);
    }

    [Fact]
    public async Task MuteAll_ShouldBroadcastForceMuted_ToOthersInGroup_ExcludingCaller()
    {
        var (hub, _, clientsMock, clientProxyMock, _, _) = CreateHub();
        var roomId = Guid.NewGuid();
        hub.Context = CreateContext(Guid.NewGuid().ToString(), "conn-mute-all");

        await hub.MuteAll(roomId);

        clientsMock.Verify(c => c.OthersInGroup($"translationRoom:{roomId}"), Times.Once);
        clientProxyMock.Verify(
            p => p.SendCoreAsync("ForceMuted", It.IsAny<object[]>(), default),
            Times.Once);
    }

    [Fact]
    public async Task OnDisconnectedAsync_ShouldPublishParticipantOffline_WhenAnotherHubConnectionRemains()
    {
        var connectionManagerMock = new Mock<IConnectionManager>();
        connectionManagerMock
            .Setup(m => m.RemoveConnection(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        var redisMock = new Mock<IConnectionMultiplexer>();
        var dbMock = new Mock<IDatabase>();
        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(dbMock.Object);

        var configMock = new Mock<Microsoft.Extensions.Configuration.IConfiguration>();
        var configSectionMock = new Mock<Microsoft.Extensions.Configuration.IConfigurationSection>();
        configSectionMock.Setup(s => s.Value).Returns("10000");
        configMock.Setup(c => c.GetSection(It.IsAny<string>())).Returns(configSectionMock.Object);

        var hub = new TranslationRoomHub(
            connectionManagerMock.Object,
            new RedisStreamService(redisMock.Object, new NullLogger<RedisStreamService>(), configMock.Object),
            new ActiveTranslationRoomRegistry(),
            redisMock.Object,
            new NullLogger<TranslationRoomHub>());

        var clientsMock = new Mock<IHubCallerClients>();
        var clientProxyMock = new Mock<IClientProxy>();
        clientsMock.Setup(c => c.OthersInGroup(It.IsAny<string>())).Returns(clientProxyMock.Object);
        hub.Clients = clientsMock.Object;
        hub.Groups = new Mock<IGroupManager>().Object;

        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        hub.Context = CreateContext(userId.ToString(), $"translation-{Guid.NewGuid()}");
        await hub.JoinTranslationRoom(roomId, "User", "en", "vi");

        await hub.OnDisconnectedAsync(null);

        dbMock.Verify(
            db => db.PublishAsync(
                RedisChannel.Literal("translationRoom:participant-offline"),
                $"{roomId}:{userId}",
                CommandFlags.None),
            Times.Once);
    }

    private static (TranslationRoomHub Hub, Mock<IDatabase> DbMock, Mock<IHubCallerClients> ClientsMock, Mock<IClientProxy> ClientProxyMock, Mock<IGroupManager> GroupsMock, Mock<IClientProxy> GroupClientProxyMock) CreateHub()
    {
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
            new NullLogger<TranslationRoomHub>());

        var clientsMock = new Mock<IHubCallerClients>();
        var clientProxyMock = new Mock<IClientProxy>();
        var groupClientProxyMock = new Mock<IClientProxy>();
        var singleClientProxyMock = new Mock<ISingleClientProxy>();
        clientsMock.Setup(c => c.Client(It.IsAny<string>())).Returns(singleClientProxyMock.Object);
        clientsMock.Setup(c => c.OthersInGroup(It.IsAny<string>())).Returns(clientProxyMock.Object);
        clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(groupClientProxyMock.Object);
        hub.Clients = clientsMock.Object;

        var groupsMock = new Mock<IGroupManager>();
        hub.Groups = groupsMock.Object;

        return (hub, dbMock, clientsMock, clientProxyMock, groupsMock, groupClientProxyMock);
    }

    private static HubCallerContext CreateContext(string userId, string connectionId)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId) };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        var claimsPrincipal = new ClaimsPrincipal(identity);

        var contextMock = new Mock<HubCallerContext>();
        contextMock.Setup(c => c.User).Returns(claimsPrincipal);
        contextMock.Setup(c => c.ConnectionId).Returns(connectionId);
        return contextMock.Object;
    }
}

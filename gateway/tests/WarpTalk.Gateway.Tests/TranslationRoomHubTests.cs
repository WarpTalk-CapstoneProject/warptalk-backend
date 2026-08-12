using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Presence;
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
            Mock.Of<IPresenceNotifier>(),
            streamService,
            translationRoomRegistry,
            redisMock.Object,
            AlwaysHost(),
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
        // Its own proxy, not the one Client(connectionId) returns: this test counts the messages
        // sent to the OLD connection, and folding the caller into the same mock would let the
        // roster this join sends to itself be counted as a ForceDisconnected.
        mockClients.Setup(c => c.Caller).Returns(new Mock<ISingleClientProxy>().Object);
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

    /// <summary>
    /// WT-354. The hub only ever announced arrivals to everyone ELSE, so a client's roster could
    /// only contain people who joined after it did. A host entering a meeting already in progress
    /// was shown a People panel holding one person: themselves.
    ///
    /// Two halves, and the second is the one that makes the first useful: the joiner must be sent
    /// the people already in the room, and must not be sent a copy of themselves — a stale entry
    /// from an earlier connection of the same user would otherwise come back as a ghost standing
    /// beside them.
    /// </summary>
    [Fact]
    public async Task JoinTranslationRoom_ShouldSendExistingRosterToCallerWithoutTheirOwnEntry()
    {
        var (hub, dbMock, clientsMock, _, _, _) = CreateHub();
        var roomId = Guid.NewGuid();
        var joiningUserId = Guid.NewGuid().ToString();
        var alreadyPresentUserId = Guid.NewGuid();

        var callerProxyMock = new Mock<ISingleClientProxy>();
        clientsMock.Setup(c => c.Caller).Returns(callerProxyMock.Object);

        var present = new ParticipantInfoDto(
            UserId: alreadyPresentUserId,
            DisplayName: "Hanh Nhi",
            SpeakLanguage: "vi",
            ListenLanguage: "en",
            IsMuted: true,
            JoinedAt: DateTime.UtcNow.AddMinutes(-5));
        var staleSelf = present with { UserId = Guid.Parse(joiningUserId), DisplayName = "Ghost" };

        dbMock.Setup(db => db.HashGetAllAsync($"translationRoom:{roomId}:participants", CommandFlags.None))
            .ReturnsAsync(new[]
            {
                new HashEntry(alreadyPresentUserId.ToString(), JsonSerializer.Serialize(present, new JsonSerializerOptions(JsonSerializerDefaults.Web))),
                new HashEntry(joiningUserId, JsonSerializer.Serialize(staleSelf, new JsonSerializerOptions(JsonSerializerDefaults.Web))),
            });

        hub.Context = CreateContext(joiningUserId, "conn-late-host");

        await hub.JoinTranslationRoom(roomId, "Tu", "vi", "en");

        callerProxyMock.Verify(
            proxy => proxy.SendCoreAsync(
                "ParticipantRoster",
                It.Is<object[]>(args =>
                    ((List<ParticipantInfoDto>)args[0]!).Count == 1 &&
                    ((List<ParticipantInfoDto>)args[0]!)[0].UserId == alreadyPresentUserId &&
                    ((List<ParticipantInfoDto>)args[0]!)[0].DisplayName == "Hanh Nhi" &&
                    ((List<ParticipantInfoDto>)args[0]!)[0].IsMuted),
                default),
            Times.Once);

        // And the joiner is recorded, or the next person to arrive inherits the same blind spot.
        dbMock.Verify(
            db => db.HashSetAsync(
                $"translationRoom:{roomId}:participants",
                joiningUserId,
                It.IsAny<RedisValue>(),
                When.Always,
                CommandFlags.None),
            Times.Once);
    }

    /// <summary>
    /// WT-354: a dropped socket must take the participant out of the LIVE roster. The database row
    /// is a separate question — ParticipantOfflineConsumerWorker marks it DISCONNECTED, which the
    /// People panel still shows — but presence is presence.
    /// </summary>
    [Fact]
    public async Task OnDisconnectedAsync_ShouldRemoveTheParticipantFromTheStoredRoster()
    {
        var (hub, dbMock, _, _, _, _) = CreateHub();
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid().ToString();
        hub.Context = CreateContext(userId, "conn-dropping");

        await hub.JoinTranslationRoom(roomId, "Tu", "vi", "en");
        await hub.OnDisconnectedAsync(null);

        dbMock.Verify(
            db => db.HashDeleteAsync(
                $"translationRoom:{roomId}:participants",
                userId,
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
            Mock.Of<IPresenceNotifier>(),
            new RedisStreamService(redisMock.Object, new NullLogger<RedisStreamService>(), configMock.Object),
            new ActiveTranslationRoomRegistry(),
            redisMock.Object,
            AlwaysHost(),
            new NullLogger<TranslationRoomHub>());

        var clientsMock = new Mock<IHubCallerClients>();
        var clientProxyMock = new Mock<IClientProxy>();
        clientsMock.Setup(c => c.OthersInGroup(It.IsAny<string>())).Returns(clientProxyMock.Object);
        // WT-354: the join below now hands the caller the room's roster.
        clientsMock.Setup(c => c.Caller).Returns(new Mock<ISingleClientProxy>().Object);
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

    /// <param name="hostAuthority">
    /// Defaults to "yes, you are the host". Every pre-existing test in this file exercises a
    /// non-host-only method (join, language, hand, reaction), so the permissive default keeps their
    /// verdicts exactly as they were; the host-authorization tests pass their own.
    /// </param>
    private static (TranslationRoomHub Hub, Mock<IDatabase> DbMock, Mock<IHubCallerClients> ClientsMock, Mock<IClientProxy> ClientProxyMock, Mock<IGroupManager> GroupsMock, Mock<IClientProxy> GroupClientProxyMock) CreateHub(
        IRoomHostAuthority? hostAuthority = null)
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
            Mock.Of<IPresenceNotifier>(),
            streamService,
            translationRoomRegistry,
            redisMock.Object,
            hostAuthority ?? AlwaysHost(),
            new NullLogger<TranslationRoomHub>());

        var clientsMock = new Mock<IHubCallerClients>();
        var clientProxyMock = new Mock<IClientProxy>();
        var groupClientProxyMock = new Mock<IClientProxy>();
        var singleClientProxyMock = new Mock<ISingleClientProxy>();
        clientsMock.Setup(c => c.Client(It.IsAny<string>())).Returns(singleClientProxyMock.Object);
        clientsMock.Setup(c => c.OthersInGroup(It.IsAny<string>())).Returns(clientProxyMock.Object);
        clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(groupClientProxyMock.Object);
        // WT-354: JoinTranslationRoom now answers the caller directly with the room's current
        // roster. Without this the fake returns null for Caller and every join test dies inside
        // SignalR's own extension method, reporting a NullReferenceException instead of whatever
        // it was actually asserting.
        clientsMock.Setup(c => c.Caller).Returns(singleClientProxyMock.Object);
        hub.Clients = clientsMock.Object;

        var groupsMock = new Mock<IGroupManager>();
        hub.Groups = groupsMock.Object;

        return (hub, dbMock, clientsMock, clientProxyMock, groupsMock, groupClientProxyMock);
    }

    // ── Host authorization ────────────────────────────────────────────────────────
    //
    // MuteAll, SpotlightParticipant and AdmitWaitingParticipant used to carry a documented KNOWN
    // GAP: they trusted the caller's JWT identity and verified nothing server-side, so any
    // authenticated participant could force-mute the whole room (the host included) or hijack
    // everyone's stage by invoking the method from the browser console. These pin both halves —
    // a non-host is refused AND no broadcast escapes, and a legitimate host still succeeds.

    [Theory]
    [InlineData("MuteAll")]
    [InlineData("SpotlightParticipant")]
    [InlineData("AdmitWaitingParticipant")]
    public async Task HostOnlyMethods_ShouldThrowAndBroadcastNothing_WhenCallerIsNotHost(string method)
    {
        var (hub, _, _, othersProxyMock, _, groupProxyMock) = CreateHub(NeverHost());
        var roomId = Guid.NewGuid();
        hub.Context = CreateContext(Guid.NewGuid().ToString(), "conn-impostor");

        await Assert.ThrowsAsync<HubException>(() => InvokeHostOnly(hub, method, roomId));

        // The refusal is only worth anything if it happens BEFORE the send. A check that threw
        // after Clients.Group(...).SendAsync would still have muted the room.
        othersProxyMock.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
        groupProxyMock.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task MuteAll_ShouldBroadcastForceMuted_WhenCallerIsHost()
    {
        var (hub, _, _, othersProxyMock, _, _) = CreateHub(AlwaysHost());
        var roomId = Guid.NewGuid();
        hub.Context = CreateContext(Guid.NewGuid().ToString(), "conn-host");

        await hub.MuteAll(roomId);

        othersProxyMock.Verify(
            p => p.SendCoreAsync("ForceMuted", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SpotlightParticipant_ShouldBroadcastSpotlightChanged_WhenCallerIsHost()
    {
        var (hub, _, _, _, _, groupProxyMock) = CreateHub(AlwaysHost());
        var roomId = Guid.NewGuid();
        hub.Context = CreateContext(Guid.NewGuid().ToString(), "conn-host");

        await hub.SpotlightParticipant(roomId, Guid.NewGuid(), true);

        groupProxyMock.Verify(
            p => p.SendCoreAsync("SpotlightChanged", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AdmitWaitingParticipant_ShouldBroadcastParticipantAdmitted_WhenCallerIsHost()
    {
        var (hub, _, _, _, _, groupProxyMock) = CreateHub(AlwaysHost());
        var roomId = Guid.NewGuid();
        hub.Context = CreateContext(Guid.NewGuid().ToString(), "conn-host");

        await hub.AdmitWaitingParticipant(roomId, Guid.NewGuid().ToString());

        groupProxyMock.Verify(
            p => p.SendCoreAsync("ParticipantAdmitted", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // The caller's own id is what gets checked — not the room id, and not anything in the payload.
    [Fact]
    public async Task HostOnlyMethods_ShouldAuthorizeTheCallersOwnIdentity()
    {
        var callerId = Guid.NewGuid().ToString();
        var roomId = Guid.NewGuid();
        var authorityMock = new Mock<IRoomHostAuthority>();
        authorityMock
            .Setup(a => a.HasHostAuthorityAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var (hub, _, _, _, _, _) = CreateHub(authorityMock.Object);
        hub.Context = CreateContext(callerId, "conn-host");

        await hub.MuteAll(roomId);

        authorityMock.Verify(
            a => a.HasHostAuthorityAsync(roomId, callerId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static Task InvokeHostOnly(TranslationRoomHub hub, string method, Guid roomId) => method switch
    {
        "MuteAll" => hub.MuteAll(roomId),
        "SpotlightParticipant" => hub.SpotlightParticipant(roomId, Guid.NewGuid(), true),
        "AdmitWaitingParticipant" => hub.AdmitWaitingParticipant(roomId, Guid.NewGuid().ToString()),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
    };

    private static IRoomHostAuthority AlwaysHost()
    {
        var mock = new Mock<IRoomHostAuthority>();
        mock.Setup(a => a.HasHostAuthorityAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return mock.Object;
    }

    private static IRoomHostAuthority NeverHost()
    {
        var mock = new Mock<IRoomHostAuthority>();
        mock.Setup(a => a.HasHostAuthorityAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        return mock.Object;
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

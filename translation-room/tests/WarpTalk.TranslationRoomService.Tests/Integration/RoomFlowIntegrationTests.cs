using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Tests.Integration;

public class RoomFlowIntegrationTests : BaseIntegrationTest
{
    [Fact]
    public async Task CreateInstantRoom_ThenJoin_ShouldSucceed()
    {
        // 1. Arrange: Prepare Create Request
        var workspaceId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var createRequest = new CreateTranslationRoomRequest(
            WorkspaceId: workspaceId,
            Title: "Integration Test Room",
            Description: "Testing the full flow",
            TranslationRoomType: TranslationRoomType.INSTANT,
            MaxParticipants: 10,
            SourceLanguage: "en",
            TargetLanguages: new List<string> { "vi", "fr" },
            Settings: null,
            ScheduledAt: null
        );

        // Set Auth Headers for Host
        Client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, hostId.ToString());

        // 2. Act: Create Room
        var createResponse = await Client.PostAsJsonAsync("/api/v1/translation-rooms", createRequest);
        var body = await createResponse.Content.ReadAsStringAsync();
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created, body);
        
        var createdRoom = await createResponse.Content.ReadFromJsonAsync<TranslationRoomDto>();
        createdRoom.Should().NotBeNull();
        createdRoom!.TranslationRoomCode.Should().NotBeNullOrWhiteSpace();
        createdRoom.Status.Should().Be("WAITING");

        // 3. Act: Join Room as Member
        var memberId = Guid.NewGuid();
        var joinRequest = new JoinTranslationRoomRequest(
            TranslationRoomCode: createdRoom.TranslationRoomCode,
            DisplayName: "Member User",
            ListenLanguage: "vi",
            SpeakLanguage: "en"
        );

        // Change Auth Header to Member
        Client.DefaultRequestHeaders.Remove(TestAuthHandler.UserIdHeader);
        Client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, memberId.ToString());

        var joinResponse = await Client.PostAsJsonAsync("/api/v1/translation-rooms/join", joinRequest);
        var joinBody = await joinResponse.Content.ReadAsStringAsync();
        
        // 4. Assert: Join Success
        joinResponse.StatusCode.Should().Be(HttpStatusCode.OK, joinBody);
        var joinData = await joinResponse.Content.ReadFromJsonAsync<JoinTranslationRoomResponse>();
        
        joinData.Should().NotBeNull();
        joinData!.Room.Id.Should().Be(createdRoom.Id);
        joinData.Participant.UserId.Should().Be(memberId);
        joinData.Participant.DisplayName.Should().Be("Member User");
        joinData.Participant.Role.Should().Be(TranslationRoomParticipantRole.PARTICIPANT);
    }

    [Fact]
    public async Task CreateScheduledRoom_ShouldHaveScheduledStatus()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        var scheduledTime = DateTime.UtcNow.AddDays(1);
        var createRequest = new CreateTranslationRoomRequest(
            WorkspaceId: Guid.NewGuid(),
            Title: "Scheduled Room",
            Description: "Testing scheduled creation",
            TranslationRoomType: TranslationRoomType.SCHEDULED,
            MaxParticipants: 5,
            SourceLanguage: "en",
            TargetLanguages: new List<string> { "vi" },
            Settings: null,
            ScheduledAt: scheduledTime
        );

        Client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, hostId.ToString());

        // Act
        var response = await Client.PostAsJsonAsync("/api/v1/translation-rooms", createRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdRoom = await response.Content.ReadFromJsonAsync<TranslationRoomDto>();
        createdRoom!.Status.Should().Be("SCHEDULED");
    }

    [Fact]
    public async Task JoinRoom_WithInvalidCode_ShouldReturnNotFound()
    {
        // Arrange
        var joinRequest = new JoinTranslationRoomRequest(
            TranslationRoomCode: "abc-defg-hij",
            DisplayName: "Ghost User",
            ListenLanguage: "en",
            SpeakLanguage: "en"
        );

        Client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, Guid.NewGuid().ToString());

        // Act
        var response = await Client.PostAsJsonAsync("/api/v1/translation-rooms/join", joinRequest);

        // Assert
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, body);
    }
}

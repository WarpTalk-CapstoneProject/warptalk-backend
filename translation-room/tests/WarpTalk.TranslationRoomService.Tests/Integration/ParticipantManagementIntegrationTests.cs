using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Enums;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Integration;

public class ParticipantManagementIntegrationTests : BaseIntegrationTest
{
    [Fact]
    public async Task GetParticipants_ShouldReturnParticipants_WhenAuthorized()
    {
        // 1. Arrange: Create room and join
        var hostId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        
        Client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, hostId.ToString());
        var createRequest = new CreateTranslationRoomRequest(Guid.NewGuid(), "Test Room", "", TranslationRoomType.INSTANT, 10, "en", new List<string> { "vi" }, null, null);
        var createResponse = await Client.PostAsJsonAsync("/api/v1/translation-rooms", createRequest);
        var createdRoom = await createResponse.Content.ReadFromJsonAsync<TranslationRoomDto>();

        Client.DefaultRequestHeaders.Remove(TestAuthHandler.UserIdHeader);
        Client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, memberId.ToString());
        await Client.PostAsJsonAsync("/api/v1/translation-rooms/join", new JoinTranslationRoomRequest(createdRoom!.TranslationRoomCode, "Member", "vi", "en"));

        // 2. Act: Get Participants as host
        Client.DefaultRequestHeaders.Remove(TestAuthHandler.UserIdHeader);
        Client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, hostId.ToString());
        var response = await Client.GetAsync($"/api/v1/translation-rooms/{createdRoom.Id}/participants");

        // 3. Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var participants = await response.Content.ReadFromJsonAsync<List<TranslationRoomParticipantDto>>();
        participants.Should().NotBeNull();
        participants!.Count.Should().Be(1); // Member only, Host hasn't joined
    }

    [Fact]
    public async Task UpdateParticipantAudio_ShouldReturnForbidden_WhenNotHost()
    {
        // 1. Arrange: Create room and join
        var hostId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var hackerId = Guid.NewGuid();
        
        Client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, hostId.ToString());
        var createRequest = new CreateTranslationRoomRequest(Guid.NewGuid(), "Test Room", "", TranslationRoomType.INSTANT, 10, "en", new List<string> { "vi" }, null, null);
        var createResponse = await Client.PostAsJsonAsync("/api/v1/translation-rooms", createRequest);
        var createdRoom = await createResponse.Content.ReadFromJsonAsync<TranslationRoomDto>();

        Client.DefaultRequestHeaders.Remove(TestAuthHandler.UserIdHeader);
        Client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, memberId.ToString());
        var joinResponse = await Client.PostAsJsonAsync("/api/v1/translation-rooms/join", new JoinTranslationRoomRequest(createdRoom!.TranslationRoomCode, "Member", "vi", "en"));
        var joinBody = await joinResponse.Content.ReadAsStringAsync();
        joinResponse.StatusCode.Should().Be(HttpStatusCode.OK, joinBody);
        var joinData = await joinResponse.Content.ReadFromJsonAsync<JoinTranslationRoomResponse>();

        // 2. Act: Try to update audio as a different user
        Client.DefaultRequestHeaders.Remove(TestAuthHandler.UserIdHeader);
        Client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, hackerId.ToString());
        var request = new UpdateParticipantAudioRequest(false);
        var response = await Client.PutAsJsonAsync($"/api/v1/translation-rooms/{createdRoom.Id}/participants/{joinData!.Participant.Id}/audio", request);

        // 3. Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task KickParticipant_ShouldSucceed_WhenHost()
    {
        // 1. Arrange: Create room and join
        var hostId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        
        Client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, hostId.ToString());
        var createRequest = new CreateTranslationRoomRequest(Guid.NewGuid(), "Test Room", "", TranslationRoomType.INSTANT, 10, "en", new List<string> { "vi" }, null, null);
        var createResponse = await Client.PostAsJsonAsync("/api/v1/translation-rooms", createRequest);
        var createdRoom = await createResponse.Content.ReadFromJsonAsync<TranslationRoomDto>();

        Client.DefaultRequestHeaders.Remove(TestAuthHandler.UserIdHeader);
        Client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, memberId.ToString());
        var joinResponse = await Client.PostAsJsonAsync("/api/v1/translation-rooms/join", new JoinTranslationRoomRequest(createdRoom!.TranslationRoomCode, "Member", "vi", "en"));
        var joinData = await joinResponse.Content.ReadFromJsonAsync<JoinTranslationRoomResponse>();

        // 2. Act: Host kicks member
        Client.DefaultRequestHeaders.Remove(TestAuthHandler.UserIdHeader);
        Client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, hostId.ToString());
        var response = await Client.PutAsync($"/api/v1/translation-rooms/{createdRoom.Id}/participants/{joinData!.Participant.Id}/kick", null);

        // 3. Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify member is kicked
        var getParticipants = await Client.GetAsync($"/api/v1/translation-rooms/{createdRoom.Id}/participants");
        var participants = await getParticipants.Content.ReadFromJsonAsync<List<TranslationRoomParticipantDto>>();
        
        var kickedMember = participants!.Find(p => p.Id == joinData.Participant.Id);
        kickedMember!.Status.Should().Be(nameof(TranslationRoomParticipantStatus.KICKED));
    }
}

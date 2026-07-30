using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WarpTalk.MeetingService.Infrastructure.Services;

namespace WarpTalk.MeetingService.Tests.Services;

public class LiveKitRoomAdminServiceTests
{
    [Fact]
    public async Task RemoveParticipantAsync_UsesRoomServiceContractAndRoomAdminGrant()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var sut = CreateService(new StubHttpMessageHandler(async request =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var result = await sut.RemoveParticipantAsync("room-1", "participant-1");

        Assert.True(result.IsSuccess);
        Assert.Equal(
            "https://warptalk-staging.livekit.cloud/twirp/livekit.RoomService/RemoveParticipant",
            capturedRequest!.RequestUri!.ToString());
        using var body = JsonDocument.Parse(capturedBody!);
        Assert.Equal("room-1", body.RootElement.GetProperty("room").GetString());
        Assert.Equal("participant-1", body.RootElement.GetProperty("identity").GetString());

        var token = capturedRequest.Headers.Authorization!.Parameter!;
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        using var grant = JsonDocument.Parse(jwt.Payload["video"].ToString()!);
        Assert.True(grant.RootElement.GetProperty("roomAdmin").GetBoolean());
        Assert.Equal("room-1", grant.RootElement.GetProperty("room").GetString());
    }

    [Fact]
    public async Task DeleteRoomAsync_IsIdempotentWhenProviderRoomIsAlreadyGone()
    {
        HttpRequestMessage? capturedRequest = null;
        var sut = CreateService(new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            Assert.Equal(
                "https://warptalk-staging.livekit.cloud/twirp/livekit.RoomService/DeleteRoom",
                request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));

        var result = await sut.DeleteRoomAsync("room-already-gone");

        Assert.True(result.IsSuccess);
        var token = capturedRequest!.Headers.Authorization!.Parameter!;
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        using var grant = JsonDocument.Parse(jwt.Payload["video"].ToString()!);
        Assert.True(grant.RootElement.GetProperty("roomCreate").GetBoolean());
        Assert.False(grant.RootElement.TryGetProperty("roomAdmin", out _));
    }

    private static LiveKitRoomAdminService CreateService(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LiveKit:Url"] = "wss://warptalk-staging.livekit.cloud",
                ["LiveKit:ApiKey"] = "test-api-key",
                ["LiveKit:ApiSecret"] = "test-api-secret-with-at-least-32-characters"
            })
            .Build();
        return new LiveKitRoomAdminService(
            new HttpClient(handler),
            configuration,
            NullLogger<LiveKitRoomAdminService>.Instance);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}

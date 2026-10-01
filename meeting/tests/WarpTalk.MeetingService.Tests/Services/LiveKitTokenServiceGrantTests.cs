using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using WarpTalk.MeetingService.Infrastructure.Services;
using Xunit;

namespace WarpTalk.MeetingService.Tests.Services;

public class LiveKitTokenServiceGrantTests
{
    private static JsonElement VideoGrant(bool canSubscribe)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LiveKit:ApiKey"] = "test-key",
                ["LiveKit:ApiSecret"] = "test-secret-test-secret-test-secret-1234"
            })
            .Build();

        var result = new LiveKitTokenService(config)
            .GenerateToken("room", "user-1", "User One", canPublish: true, canSubscribe: canSubscribe);

        Assert.True(result.IsSuccess);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.Value);
        return JsonDocument.Parse(JsonSerializer.Serialize(jwt.Payload["video"])).RootElement;
    }

    [Fact]
    public void Subscribing_participant_can_update_own_metadata()
    {
        var grant = VideoGrant(canSubscribe: true);
        Assert.True(grant.TryGetProperty("canUpdateOwnMetadata", out var v));
        Assert.True(v.GetBoolean());
    }

    [Fact]
    public void Non_subscribing_participant_does_not_get_metadata_grant()
    {
        var grant = VideoGrant(canSubscribe: false);
        Assert.False(grant.TryGetProperty("canUpdateOwnMetadata", out _));
    }
}

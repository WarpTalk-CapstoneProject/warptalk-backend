using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WarpTalk.MeetingService.Infrastructure.Services;

namespace WarpTalk.MeetingService.Tests.Services;

public class LiveKitEgressServiceTests
{
    [Fact]
    public async Task StartRoomCompositeEgressAsync_UsesCloudHttpsEndpointAndS3Output()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new StubHttpMessageHandler(async request =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"egress_id":"EG_cloud_123"}""")
            };
        });
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["LiveKit:Url"] = "wss://warptalk-staging.livekit.cloud",
            ["LiveKit:ApiKey"] = "test-api-key",
            ["LiveKit:ApiSecret"] = "test-api-secret-with-at-least-32-characters",
            ["LiveKit:Egress:S3:AccessKey"] = "storage-access",
            ["LiveKit:Egress:S3:Secret"] = "storage-secret",
            ["LiveKit:Egress:S3:Bucket"] = "warptalk-recordings",
            ["LiveKit:Egress:S3:Region"] = "ap-southeast-1",
            ["LiveKit:Egress:S3:Endpoint"] = "https://storage.example.com"
        });
        var sut = new LiveKitEgressService(
            new HttpClient(handler),
            configuration,
            NullLogger<LiveKitEgressService>.Instance);

        var result = await sut.StartRoomCompositeEgressAsync("room-1");

        Assert.True(result.IsSuccess);
        Assert.Equal("EG_cloud_123", result.Value);
        Assert.Equal(
            "https://warptalk-staging.livekit.cloud/twirp/livekit.Egress/StartRoomCompositeEgress",
            capturedRequest!.RequestUri!.ToString());

        using var payload = JsonDocument.Parse(capturedBody!);
        var fileOutput = payload.RootElement.GetProperty("file_outputs")[0];
        var s3 = fileOutput.GetProperty("s3");
        Assert.Equal("warptalk-recordings", s3.GetProperty("bucket").GetString());
        Assert.Equal("ap-southeast-1", s3.GetProperty("region").GetString());
        Assert.Equal("https://storage.example.com", s3.GetProperty("endpoint").GetString());
        Assert.True(s3.GetProperty("force_path_style").GetBoolean());
    }

    [Fact]
    public void Constructor_RejectsInsecureCloudStorageEndpoint()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["LiveKit:Url"] = "wss://warptalk-staging.livekit.cloud",
            ["LiveKit:ApiKey"] = "test-api-key",
            ["LiveKit:ApiSecret"] = "test-api-secret-with-at-least-32-characters",
            ["LiveKit:Egress:S3:AccessKey"] = "storage-access",
            ["LiveKit:Egress:S3:Secret"] = "storage-secret",
            ["LiveKit:Egress:S3:Bucket"] = "warptalk-recordings",
            ["LiveKit:Egress:S3:Endpoint"] = "http://minio:9000"
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new LiveKitEgressService(
                new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))),
                configuration,
                NullLogger<LiveKitEgressService>.Instance));

        Assert.Contains("HTTPS", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>
    /// The production defect this pins, found 2026-08-20 by reading prod.
    ///
    /// LiveKit answers an unknown egress with 404 + {"code":"not_found"}. This method used to
    /// return Failure for it, EgressReconciliationService reads a failed lookup as "LiveKit
    /// unreachable" and deliberately leaves the room alone — so its UnknownEgressGrace path,
    /// written for exactly the aged-out case, could never run. Five rooms had been holding an
    /// ActiveEgressId ever since, and the service logged 2,955 egress lines in 24 hours asking
    /// about egresses that will never exist again.
    ///
    /// The interface has always documented the contract: "Not knowing an id is a normal answer,
    /// not a failure." The implementation was guessing the wrong wire shape for it.
    /// </summary>
    [Fact]
    public async Task GetEgressAsync_TreatsLiveKitsNotFoundAsAnAnswer_NotAFailure()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    """{"code":"not_found","msg":"object cannot be found"}""")
            }));

        var result = await BuildService(handler).GetEgressAsync("EG_knxfkj8TECU3");

        // Success carrying null is what lets the caller clear the room after its grace period.
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task GetEgressAsync_TrustsTheTwirpBodyEvenWhenTheStatusIsNot404()
    {
        // A gateway in front of LiveKit can rewrite the status; the body's own code is the
        // authoritative statement.
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"code":"not_found","msg":"object cannot be found"}""")
            }));

        var result = await BuildService(handler).GetEgressAsync("EG_gone");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task GetEgressAsync_StillFailsWhenLiveKitIsGenuinelyUnreachable()
    {
        // The distinction that matters: clearing a room on a transport failure would tell a host
        // their live recording had stopped when it had not.
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("upstream unavailable")
            }));

        var result = await BuildService(handler).GetEgressAsync("EG_live");

        Assert.False(result.IsSuccess);
    }

    private static LiveKitEgressService BuildService(StubHttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            BuildConfiguration(new Dictionary<string, string?>
            {
                ["LiveKit:Url"] = "wss://warptalk-staging.livekit.cloud",
                ["LiveKit:ApiKey"] = "test-api-key",
                ["LiveKit:ApiSecret"] = "test-api-secret-with-at-least-32-characters"
            }),
            NullLogger<LiveKitEgressService>.Instance);

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}

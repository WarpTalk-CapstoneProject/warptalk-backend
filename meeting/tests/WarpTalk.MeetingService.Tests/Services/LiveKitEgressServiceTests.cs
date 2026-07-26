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

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}

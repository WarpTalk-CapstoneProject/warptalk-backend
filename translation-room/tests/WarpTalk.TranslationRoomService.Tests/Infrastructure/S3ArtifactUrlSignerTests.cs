using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using WarpTalk.TranslationRoomService.Infrastructure.Storage;

namespace WarpTalk.TranslationRoomService.Tests.Infrastructure;

public sealed class S3ArtifactUrlSignerTests
{
    [Fact]
    public async Task CreateDownloadUrlAsync_SignsS3ObjectWithoutNetworkCall()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LiveKit:Egress:S3:AccessKey"] = "test-access-key",
                ["LiveKit:Egress:S3:Secret"] = "test-secret-key",
                ["LiveKit:Egress:S3:Endpoint"] = "https://r2.example.test",
                ["LiveKit:Egress:S3:Region"] = "auto"
            })
            .Build();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(item => item.EnvironmentName).Returns("Development");
        using var signer = new S3ArtifactUrlSigner(configuration, environment.Object);

        var url = await signer.CreateDownloadUrlAsync(
            "s3://recordings/rooms/demo.mp4",
            TimeSpan.FromMinutes(15));

        Assert.StartsWith("https://r2.example.test/", url);
        Assert.Contains("X-Amz-Signature=", url);
        Assert.Contains("recordings/rooms/demo.mp4", url);
    }

    [Fact]
    public void Constructor_RejectsMissingProductionCredentials()
    {
        var configuration = new ConfigurationBuilder().Build();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(item => item.EnvironmentName).Returns(Environments.Production);

        Assert.Throws<InvalidOperationException>(
            () => new S3ArtifactUrlSigner(configuration, environment.Object));
    }

    [Fact]
    public async Task CreateDownloadUrlAsync_PreservesHttpForLocalS3CompatibleEndpoint()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LiveKit:Egress:S3:AccessKey"] = "test-access-key",
                ["LiveKit:Egress:S3:Secret"] = "test-secret-key",
                ["LiveKit:Egress:S3:Endpoint"] = "http://minio:9000",
                ["LiveKit:Egress:S3:Region"] = "us-east-1"
            })
            .Build();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(item => item.EnvironmentName).Returns(Environments.Development);
        using var signer = new S3ArtifactUrlSigner(configuration, environment.Object);

        var url = await signer.CreateDownloadUrlAsync(
            "s3://recordings/rooms/demo.mp4",
            TimeSpan.FromMinutes(15));

        Assert.StartsWith("http://minio:9000/", url);
    }
}

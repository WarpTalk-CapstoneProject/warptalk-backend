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

    /// <summary>
    /// WT-655: the shape LiveKit egress actually stores. <c>fileResults[].location</c> is an
    /// https object URL, and this used to be handed back unsigned — a 403 the player showed as
    /// nothing at all.
    /// </summary>
    [Fact]
    public async Task CreateDownloadUrlAsync_SignsHttpsPathStyleUrlOnConfiguredEndpoint()
    {
        using var signer = CreateSigner("https://r2.example.test");

        var url = await signer.CreateDownloadUrlAsync(
            "https://r2.example.test/recordings/rooms/demo.mp4",
            TimeSpan.FromMinutes(15));

        Assert.StartsWith("https://r2.example.test/recordings/", url);
        Assert.Contains("rooms/demo.mp4", url);
        Assert.Contains("X-Amz-Signature=", url);
        Assert.Contains("X-Amz-Expires=", url);
    }

    /// <summary>
    /// WT-655: egress can report the object over http for an endpoint we address over https. Neither
    /// URL states a port, so both carry a default invented from the scheme (80 against 443) — and
    /// treating that as a different address would send the recording down the foreign-host path and
    /// throw, for a file sitting in our own bucket.
    /// </summary>
    [Fact]
    public async Task CreateDownloadUrlAsync_SignsUrlWhoseSchemeDiffersFromTheEndpoint()
    {
        using var signer = CreateSigner("https://r2.example.test");

        var url = await signer.CreateDownloadUrlAsync(
            "http://r2.example.test/recordings/rooms/demo.mp4",
            TimeSpan.FromMinutes(15));

        Assert.Contains("rooms/demo.mp4", url);
        Assert.Contains("X-Amz-Signature=", url);
    }

    /// <summary>
    /// The other half of that rule: a port both sides actually state is a real address, and a
    /// mismatch there is a different host — not a default filled in on our behalf.
    /// </summary>
    [Fact]
    public async Task CreateDownloadUrlAsync_RejectsAStatedPortThatDoesNotMatch()
    {
        using var signer = CreateSigner("http://minio:9000");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.CreateDownloadUrlAsync(
                "http://minio:9001/recordings/rooms/demo.mp4",
                TimeSpan.FromMinutes(15)));

        Assert.Contains("foreign host", error.Message);
    }

    [Fact]
    public async Task CreateDownloadUrlAsync_SignsHttpPathStyleUrlOnConfiguredEndpointWithPort()
    {
        using var signer = CreateSigner("http://minio:9000");

        var url = await signer.CreateDownloadUrlAsync(
            "http://minio:9000/recordings/rooms/demo%20one.mp4",
            TimeSpan.FromMinutes(15));

        Assert.StartsWith("http://minio:9000/recordings/", url);
        Assert.Contains("X-Amz-Signature=", url);
    }

    [Fact]
    public async Task CreateDownloadUrlAsync_SignsVirtualHostStyleUrlOnConfiguredEndpoint()
    {
        using var signer = CreateSigner("https://r2.example.test");

        var url = await signer.CreateDownloadUrlAsync(
            "https://recordings.r2.example.test/rooms/demo.mp4",
            TimeSpan.FromMinutes(15));

        Assert.Contains("recordings", url);
        Assert.Contains("rooms/demo.mp4", url);
        Assert.Contains("X-Amz-Signature=", url);
    }

    [Fact]
    public async Task CreateDownloadUrlAsync_RejectsForeignHostInsteadOfReturningItUnsigned()
    {
        using var signer = CreateSigner("https://r2.example.test");
        const string foreign = "https://cdn.someone-else.test/recordings/rooms/demo.mp4";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.CreateDownloadUrlAsync(foreign, TimeSpan.FromMinutes(15)));

        // The point of the fix: the caller must be able to tell WHY, and must never be able to
        // mistake an unsignable URL for a working one.
        Assert.Contains("foreign host", error.Message);
        Assert.Contains("r2.example.test", error.Message);
        Assert.DoesNotContain("not absolute", error.Message);
    }

    [Fact]
    public async Task CreateDownloadUrlAsync_RejectsNonAbsoluteStoredUrlWithItsOwnCause()
    {
        using var signer = CreateSigner("https://r2.example.test");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.CreateDownloadUrlAsync("/out/recordings/demo.mp4", TimeSpan.FromMinutes(15)));

        Assert.Contains("not absolute", error.Message);
        Assert.Contains("/out/recordings/demo.mp4", error.Message);
        Assert.DoesNotContain("foreign host", error.Message);
    }

    [Fact]
    public async Task CreateDownloadUrlAsync_RejectsUnsupportedSchemeWithItsOwnCause()
    {
        using var signer = CreateSigner("https://r2.example.test");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.CreateDownloadUrlAsync(
                "file:///out/recordings/demo.mp4",
                TimeSpan.FromMinutes(15)));

        Assert.Contains("scheme 'file'", error.Message);
        Assert.DoesNotContain("foreign host", error.Message);
        Assert.DoesNotContain("not absolute", error.Message);
    }

    private static S3ArtifactUrlSigner CreateSigner(string endpoint)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LiveKit:Egress:S3:AccessKey"] = "test-access-key",
                ["LiveKit:Egress:S3:Secret"] = "test-secret-key",
                ["LiveKit:Egress:S3:Endpoint"] = endpoint,
                ["LiveKit:Egress:S3:Region"] = "auto"
            })
            .Build();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(item => item.EnvironmentName).Returns(Environments.Development);
        return new S3ArtifactUrlSigner(configuration, environment.Object);
    }
}

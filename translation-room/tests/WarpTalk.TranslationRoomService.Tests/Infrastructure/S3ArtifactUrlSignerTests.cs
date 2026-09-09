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

    /// <summary>
    /// WT-644 — THE SHAPE A REAL RECORDING ACTUALLY ARRIVES IN.
    ///
    /// Nothing writes <c>s3://</c>. RecordingCompletedEventProcessor is the only writer of a
    /// non-null FileUrl and it stores <c>EgressInfo.fileResults[].location</c> verbatim, which for
    /// an S3/R2 destination is the uploader's HTTPS object URL. The signer used to hand exactly
    /// that back unsigned, so the &lt;video&gt; element on the record page fetched R2's S3 API
    /// endpoint with no credentials and got a 401 — no player, for every recording ever made.
    /// </summary>
    [Fact]
    public async Task CreateDownloadUrlAsync_SignsThePathStyleHttpsLocationLiveKitReturns()
    {
        using var signer = Signer("https://r2.example.test");

        var url = await signer.CreateDownloadUrlAsync(
            "https://r2.example.test/warptalk-recordings/recordings/room-123-20260908.mp4",
            TimeSpan.FromMinutes(15));

        Assert.Contains("X-Amz-Signature=", url);
        Assert.Contains("recordings/room-123-20260908.mp4", url);
        // The bucket must not end up doubled into the key — path style already carries it.
        Assert.DoesNotContain("warptalk-recordings/warptalk-recordings", url);
    }

    /// <summary>AWS's own spelling of the same object, which has the bucket in the host.</summary>
    [Fact]
    public async Task CreateDownloadUrlAsync_SignsTheVirtualHostStyleHttpsLocation()
    {
        using var signer = Signer("https://r2.example.test");

        var url = await signer.CreateDownloadUrlAsync(
            "https://warptalk-recordings.r2.example.test/recordings/room-123.mp4",
            TimeSpan.FromMinutes(15));

        Assert.Contains("X-Amz-Signature=", url);
        Assert.Contains("recordings/room-123.mp4", url);
    }

    /// <summary>
    /// A URL this service cannot attribute to its own bucket must be REFUSED, not passed through.
    /// Passing it through is either a link that does not work (private object) or a permanent,
    /// un-expiring link to a meeting recording (public object) — and this method's whole job is to
    /// mint short-lived credentialed links.
    /// </summary>
    [Fact]
    public async Task CreateDownloadUrlAsync_RefusesAUrlOutsideTheConfiguredBucket()
    {
        using var signer = Signer("https://r2.example.test");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.CreateDownloadUrlAsync(
                "https://someone-elses-host.example/public/room-123.mp4",
                TimeSpan.FromMinutes(15)));
    }

    /// <summary>
    /// WT-655 — the OTHER shape a failed egress leaves behind, and the one whose cause is easiest
    /// to misread. When the upload never happened, `fileResults[].location` is absent and
    /// EgressCompletion falls back to `filename`: a path inside the egress container, which names
    /// no host and no bucket. It has to fail distinguishably from a foreign-host URL, because the
    /// two call for completely different investigations — one is a broken upload, the other is a
    /// misconfigured bucket.
    /// </summary>
    [Fact]
    public async Task CreateDownloadUrlAsync_RejectsAContainerLocalPathWithItsOwnCause()
    {
        using var signer = Signer("https://r2.example.test");

        const string storedUrl = "/out/recordings/room-123.mp4";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.CreateDownloadUrlAsync(storedUrl, TimeSpan.FromMinutes(15)));

        // The property, not a particular sentence: whatever wording each platform reaches, it must
        // not blame the bucket. Windows stops at "not absolute"; Linux parses the rooted path as a
        // file:// URI and reaches the filesystem-path guard. Both are the right investigation.
        Assert.DoesNotContain("bucket", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            error.Message.Contains("not absolute", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("filesystem path", StringComparison.OrdinalIgnoreCase),
            $"Expected a path-shaped cause, got: {error.Message}");
    }

    private static S3ArtifactUrlSigner Signer(string endpoint)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LiveKit:Egress:S3:AccessKey"] = "test-access-key",
                ["LiveKit:Egress:S3:Secret"] = "test-secret-key",
                ["LiveKit:Egress:S3:Endpoint"] = endpoint,
                ["LiveKit:Egress:S3:Region"] = "auto",
                ["LiveKit:Egress:S3:Bucket"] = "warptalk-recordings"
            })
            .Build();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(item => item.EnvironmentName).Returns("Development");
        return new S3ArtifactUrlSigner(configuration, environment.Object);
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

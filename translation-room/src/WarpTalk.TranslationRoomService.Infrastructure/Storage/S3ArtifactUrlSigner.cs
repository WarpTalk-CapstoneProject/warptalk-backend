using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Storage;

public sealed class S3ArtifactUrlSigner : IArtifactUrlSigner, IDisposable
{
    private readonly IAmazonS3? _s3;
    private readonly Protocol _protocol = Protocol.HTTPS;

    public S3ArtifactUrlSigner(IConfiguration configuration, IHostEnvironment environment)
    {
        var accessKey = configuration["LiveKit:Egress:S3:AccessKey"];
        var secretKey = configuration["LiveKit:Egress:S3:Secret"];
        var endpoint = configuration["LiveKit:Egress:S3:Endpoint"];
        var region = configuration["LiveKit:Egress:S3:Region"];

        if (environment.IsProduction() &&
            (string.IsNullOrWhiteSpace(accessKey) ||
             string.IsNullOrWhiteSpace(secretKey) ||
             string.IsNullOrWhiteSpace(endpoint)))
        {
            throw new InvalidOperationException(
                "LiveKit:Egress:S3 credentials and endpoint are required in Production.");
        }

        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            return;

        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) &&
            endpointUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            _protocol = Protocol.HTTP;
        }

        var config = new AmazonS3Config
        {
            ForcePathStyle = true,
            ServiceURL = endpoint,
            AuthenticationRegion = string.IsNullOrWhiteSpace(region) ? "auto" : region
        };
        _s3 = new AmazonS3Client(accessKey, secretKey, config);
    }

    public Task<string> CreateDownloadUrlAsync(
        string storedUrl,
        TimeSpan lifetime,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(storedUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Artifact URL is not absolute.");

        if (!uri.Scheme.Equals("s3", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(storedUrl);

        if (_s3 is null)
            throw new InvalidOperationException("S3 signing credentials are not configured.");

        var bucket = uri.Host;
        var key = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Artifact S3 URL must contain a bucket and object key.");

        var signed = _s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Expires = DateTime.UtcNow.Add(lifetime),
            Verb = HttpVerb.GET,
            Protocol = _protocol
        });
        return Task.FromResult(signed);
    }

    public void Dispose() => _s3?.Dispose();
}

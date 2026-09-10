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
    /// <summary>
    /// The bucket LiveKit Egress uploads into. Read here so an <c>https://</c> location can be
    /// recognised as ours — see <see cref="TryResolveObject"/>.
    /// </summary>
    private readonly string? _bucket;

    public S3ArtifactUrlSigner(IConfiguration configuration, IHostEnvironment environment)
    {
        var accessKey = configuration["LiveKit:Egress:S3:AccessKey"];
        var secretKey = configuration["LiveKit:Egress:S3:Secret"];
        var endpoint = configuration["LiveKit:Egress:S3:Endpoint"];
        var region = configuration["LiveKit:Egress:S3:Region"];
        _bucket = configuration["LiveKit:Egress:S3:Bucket"]?.Trim();

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

        // WT-655 — a filesystem path is a BROKEN UPLOAD, and must not be reported as a bucket
        // misconfiguration. When egress uploads nothing, EgressCompletion falls back from
        // `fileResults[].location` to `filename`, which is a path inside the egress container.
        //
        // The check above does not catch it on the platform that matters. On Windows "/out/rec.mp4"
        // is not an absolute URI and stops there; on Linux — where this service actually runs —
        // .NET reads a rooted path as file:///out/rec.mp4, an absolute URI, so it sailed past and
        // came out the other end as "does not belong to the configured LiveKit Egress bucket". That
        // sends whoever reads the log to check bucket configuration that was never wrong, for a
        // recording that was never uploaded. Two different faults, two different investigations.
        if (uri.IsFile || string.IsNullOrEmpty(uri.Host))
        {
            throw new InvalidOperationException(
                $"Artifact URL '{storedUrl}' is a filesystem path, not an object in storage. The "
                + "recording was written inside the egress container and never uploaded, so there "
                + "is nothing to sign.");
        }

        if (!TryResolveObject(uri, out var bucket, out var key))
        {
            // NOT "return it as it is". This method exists to mint a credentialed link to private
            // object storage, and an unsigned one is never a correct answer to that: either the
            // object is private and the answer does not work, or it is public and the answer hands
            // out a permanent link to a meeting recording. Refusing says which artifact is
            // unreadable and why, in a log, instead of rendering a broken <video> element.
            throw new InvalidOperationException(
                $"Artifact URL '{uri.Scheme}://{uri.Host}' does not belong to the configured "
                + "LiveKit Egress bucket, so no download link can be signed for it.");
        }

        if (_s3 is null)
            throw new InvalidOperationException("S3 signing credentials are not configured.");

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

    /// <summary>
    /// The bucket and object key behind a stored artifact URL, in either of the two shapes that
    /// reach this row.
    /// </summary>
    /// <remarks>
    /// WT-644 — WHY <c>https://</c> IS HANDLED AT ALL.
    ///
    /// This used to sign only <c>s3://bucket/key</c> and hand anything else back verbatim. Nothing
    /// ever writes an <c>s3://</c> URL. <c>RecordingCompletedEventProcessor</c> is the ONLY writer
    /// of a non-null <c>FileUrl</c> (ArtifactsFinalizer passes null for every artifact it creates),
    /// and the value it stores is <c>EgressInfo.fileResults[].location</c> — which for an S3 or R2
    /// destination is the uploader's own HTTPS object URL, not a scheme our code chose.
    ///
    /// So every real recording took the passthrough branch. What the player received was
    /// <c>https://&lt;account&gt;.r2.cloudflarestorage.com/warptalk-recordings/…</c> — the S3 API
    /// endpoint, which serves nothing without a signature — and the &lt;video&gt; element got a 401.
    /// That is the "no player" half of the reported bug, and it is broken playback rather than a
    /// leak ONLY because R2's S3 endpoint is never public; against a bucket that had been made
    /// world-readable the identical code would have been handing out permanent links instead.
    ///
    /// Attribution is by BUCKET rather than by host: the same bucket is reachable path-style
    /// (<c>https://endpoint/bucket/key</c>, which is what force_path_style produces — see
    /// LiveKitEgressService.BuildS3Output) and virtual-host style
    /// (<c>https://bucket.endpoint/key</c>, which AWS produces), and the bucket name is the one
    /// thing both spellings agree on and that we can check against our own configuration.
    /// </remarks>
    private bool TryResolveObject(Uri uri, out string bucket, out string key)
    {
        bucket = string.Empty;
        key = string.Empty;

        // s3://bucket/key — the shape our own tests and any hand-written row use. The authority IS
        // the bucket, so nothing has to be recognised.
        if (uri.Scheme.Equals("s3", StringComparison.OrdinalIgnoreCase))
        {
            bucket = uri.Host;
            key = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            return !string.IsNullOrWhiteSpace(bucket) && !string.IsNullOrWhiteSpace(key);
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_bucket)) return false;

        var path = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));

        // Path style: https://<endpoint>/<bucket>/<key>
        var pathPrefix = _bucket + "/";
        if (path.StartsWith(pathPrefix, StringComparison.Ordinal) && path.Length > pathPrefix.Length)
        {
            bucket = _bucket;
            key = path[pathPrefix.Length..];
            return true;
        }

        // Virtual-host style: https://<bucket>.<endpoint>/<key>
        if (uri.Host.StartsWith(_bucket + ".", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(path))
        {
            bucket = _bucket;
            key = path;
            return true;
        }

        return false;
    }

    public void Dispose() => _s3?.Dispose();
}

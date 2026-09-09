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
    /// The configured S3 endpoint, kept so an already-resolved <c>http(s)://</c> object URL can be
    /// recognised as OURS and re-signed. Null whenever <see cref="_s3"/> is null (no credentials) or
    /// the configured endpoint is not an absolute URI.
    /// </summary>
    private readonly Uri? _endpoint;

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

        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            _endpoint = endpointUri;
            if (endpointUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Presigns the stored artifact URL for <paramref name="lifetime"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WT-655: this used to presign only <c>s3://bucket/key</c> and, for every other shape, hand the
    /// stored URL straight back. LiveKit egress stores <c>fileResults[].location</c>, which is an
    /// <c>https://…</c> object URL, so recordings took that fall-through path and the web got a bare
    /// link to a private bucket: the bucket answered 403 and the <c>&lt;video&gt;</c> element failed
    /// with nothing in the log. Returning an unsigned URL is never an acceptable fallback here — the
    /// bucket is private, so an unsigned link cannot work, and because it LOOKS like a URL it makes a
    /// configuration fault indistinguishable from success all the way out to the player. A URL this
    /// class cannot sign is a fault, and it is thrown so the caller logs it.
    /// </para>
    /// <para>
    /// Both addressing styles are accepted for our own endpoint because the client runs with
    /// <c>ForcePathStyle = true</c> (so it writes <c>https://endpoint/bucket/key</c>) while some
    /// S3-compatible gateways report the virtual-host form <c>https://bucket.endpoint/key</c> back.
    /// Host and port identify the endpoint; the scheme is not compared, because egress may report
    /// http for an endpoint we address over https (or the reverse) and the scheme we sign with is
    /// <see cref="_protocol"/>, taken from configuration rather than from the stored URL.
    /// </para>
    /// </remarks>
    public Task<string> CreateDownloadUrlAsync(
        string storedUrl,
        TimeSpan lifetime,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(storedUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                $"Artifact URL is not absolute ('{storedUrl}'), so it names no bucket or object key " +
                "to sign. A container-local egress path is the usual cause: the recording was written " +
                "to the egress container's filesystem instead of being uploaded to S3.");
        }

        string bucket;
        string key;

        if (uri.Scheme.Equals("s3", StringComparison.OrdinalIgnoreCase))
        {
            bucket = uri.Host;
            key = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        }
        else if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                 uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            if (_endpoint is null)
            {
                throw new InvalidOperationException(
                    "S3 signing credentials are not configured, so the object URL " +
                    $"'{uri.GetLeftPart(UriPartial.Path)}' cannot be checked against this service's " +
                    "S3 endpoint or signed.");
            }

            if (!TryResolveOwnObjectUrl(uri, _endpoint, out bucket, out key))
            {
                throw new InvalidOperationException(
                    $"Artifact URL '{uri.GetLeftPart(UriPartial.Path)}' is on a foreign host: it does " +
                    $"not point at this service's configured S3 endpoint ('{_endpoint.Host}:{_endpoint.Port}'), " +
                    "so this service holds no credentials that could sign it.");
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"Artifact URL scheme '{uri.Scheme}' is not supported; expected s3:// or an http(s) " +
                "object URL on this service's configured S3 endpoint.");
        }

        if (_s3 is null)
            throw new InvalidOperationException("S3 signing credentials are not configured.");

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

    /// <summary>
    /// Splits an <c>http(s)</c> object URL into bucket and key when — and only when — it addresses
    /// the configured endpoint. Returns false for anything else, so the caller can fail loudly
    /// instead of handing back a link nobody can open.
    /// </summary>
    private static bool TryResolveOwnObjectUrl(Uri uri, Uri endpoint, out string bucket, out string key)
    {
        bucket = string.Empty;
        key = string.Empty;

        if (uri.Port != endpoint.Port)
            return false;

        // Path-style (what ForcePathStyle writes): https://endpoint/bucket/key…
        if (uri.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase))
        {
            // Split the bucket off BEFORE unescaping: a key may legitimately contain an escaped
            // '%2F', which must stay part of the key rather than becoming a path separator.
            var path = uri.AbsolutePath.TrimStart('/');
            var separator = path.IndexOf('/');
            if (separator <= 0 || separator == path.Length - 1)
                return false;

            bucket = Uri.UnescapeDataString(path[..separator]);
            key = Uri.UnescapeDataString(path[(separator + 1)..]);
            return true;
        }

        // Virtual-host style: https://bucket.endpoint/key…
        var suffix = "." + endpoint.Host;
        if (uri.Host.Length > suffix.Length &&
            uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            bucket = uri.Host[..^suffix.Length];
            key = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            return key.Length > 0;
        }

        return false;
    }

    public void Dispose() => _s3?.Dispose();
}

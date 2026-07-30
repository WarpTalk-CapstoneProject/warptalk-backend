using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared.Configuration;

namespace WarpTalk.AuthService.Infrastructure.Storage;

public sealed class S3VoiceSampleStorage : IVoiceSampleStorage
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public S3VoiceSampleStorage(
        IAmazonS3 s3,
        IOptions<ObjectStorageOptions> options)
    {
        _s3 = s3;
        _bucket = options.Value.S3.BucketName
            ?? throw new InvalidOperationException("Storage:S3:BucketName is required.");
    }

    public async Task<string> SaveAsync(
        string storageKey,
        Stream contentStream,
        CancellationToken ct = default)
    {
        ValidateKey(storageKey);
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = storageKey,
            InputStream = contentStream,
            AutoCloseStream = false,
            ContentType = "audio/octet-stream"
        }, ct);
        return storageKey;
    }

    public async Task<Stream> ReadAsync(
        string storageKey,
        CancellationToken ct = default)
    {
        ValidateKey(storageKey);
        using var response = await _s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _bucket,
            Key = storageKey
        }, ct);
        var result = new MemoryStream();
        await response.ResponseStream.CopyToAsync(result, ct);
        result.Position = 0;
        return result;
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        ValidateKey(storageKey);
        return _s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = _bucket,
            Key = storageKey
        }, ct);
    }

    private static void ValidateKey(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey)
            || storageKey.StartsWith('/')
            || storageKey.Contains('\\')
            || storageKey.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Voice sample storage key is invalid.", nameof(storageKey));
        }
    }
}

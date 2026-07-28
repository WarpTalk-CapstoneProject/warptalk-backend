using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.Extensions.Options;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.Shared.Configuration;

namespace WarpTalk.MeetingService.Infrastructure.Storage;

public sealed class S3MeetingChatFileStorage : IMeetingChatFileStorage
{
    private const int MaximumFileSizeBytes = 25 * 1024 * 1024;

    private readonly IAmazonS3 _s3;
    private readonly S3ObjectStorageOptions _options;

    public S3MeetingChatFileStorage(
        IAmazonS3 s3,
        IOptions<ObjectStorageOptions> options)
    {
        _s3 = s3;
        _options = options.Value.S3;
    }

    public async Task SaveAsync(
        string storageKey,
        Stream contentStream,
        CancellationToken ct = default)
    {
        ValidateStorageKey(storageKey);
        if (_options.EnsureBucketExists
            && !await AmazonS3Util.DoesS3BucketExistV2Async(_s3, BucketName))
        {
            await _s3.PutBucketAsync(
                new PutBucketRequest { BucketName = BucketName },
                ct);
        }

        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = BucketName,
            Key = storageKey,
            InputStream = contentStream,
            AutoCloseStream = false,
            ContentType = "application/octet-stream"
        }, ct);
    }

    public async Task<Stream> OpenReadAsync(
        string storageKey,
        CancellationToken ct = default)
    {
        ValidateStorageKey(storageKey);
        using var response = await _s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = BucketName,
            Key = storageKey
        }, ct);

        var result = new MemoryStream();
        await CopyWithLimitAsync(response.ResponseStream, result, ct);
        result.Position = 0;
        return result;
    }

    public Task DeleteAsync(
        string storageKey,
        CancellationToken ct = default)
    {
        ValidateStorageKey(storageKey);
        return _s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = BucketName,
            Key = storageKey
        }, ct);
    }

    private string BucketName =>
        _options.BucketName
        ?? throw new InvalidOperationException(
            "Storage:S3:BucketName is required for meeting chat file storage.");

    private static void ValidateStorageKey(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey)
            || storageKey.StartsWith('/')
            || storageKey.Contains('\\')
            || storageKey.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException(
                "The meeting chat storage key is invalid.",
                nameof(storageKey));
        }
    }

    private static async Task CopyWithLimitAsync(
        Stream source,
        MemoryStream destination,
        CancellationToken ct)
    {
        var buffer = new byte[81920];
        var total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0)
            {
                return;
            }

            total += read;
            if (total > MaximumFileSizeBytes)
            {
                throw new InvalidDataException(
                    "Meeting chat file exceeds the 25 MB limit.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}

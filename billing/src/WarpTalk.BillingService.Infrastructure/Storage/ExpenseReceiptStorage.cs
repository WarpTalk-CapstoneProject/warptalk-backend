using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared.Configuration;

namespace WarpTalk.BillingService.Infrastructure.Storage;

/// <summary>
/// Receipts of operating expenses (G12) in the platform's S3-compatible storage — the in-cluster MinIO
/// in production, through the same Storage:* settings the auth, meeting and workspace services read.
/// </summary>
public sealed class S3ExpenseReceiptStorage : IExpenseReceiptStorage
{
    private readonly IAmazonS3 _s3;
    private readonly S3ObjectStorageOptions _options;
    private bool _bucketChecked;

    public S3ExpenseReceiptStorage(IAmazonS3 s3, S3ObjectStorageOptions options)
    {
        _s3 = s3;
        _options = options;
    }

    public bool IsAvailable => true;

    private string Bucket => _options.BucketName!;

    public async Task SaveAsync(string storageKey, Stream content, string contentType, CancellationToken ct = default)
    {
        ReceiptKeys.Validate(storageKey);
        if (_options.EnsureBucketExists && !_bucketChecked)
        {
            if (!await AmazonS3Util.DoesS3BucketExistV2Async(_s3, Bucket))
            {
                await _s3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, ct);
            }

            _bucketChecked = true;
        }

        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = storageKey,
            InputStream = content,
            AutoCloseStream = false,
            ContentType = contentType,
        }, ct);
    }

    public async Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default)
    {
        ReceiptKeys.Validate(storageKey);
        try
        {
            using var response = await _s3.GetObjectAsync(new GetObjectRequest { BucketName = Bucket, Key = storageKey }, ct);
            var copy = new MemoryStream();
            await response.ResponseStream.CopyToAsync(copy, ct);
            copy.Position = 0;
            return copy;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        ReceiptKeys.Validate(storageKey);
        return _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = Bucket, Key = storageKey }, ct);
    }
}

/// <summary>Development only: receipts under ./uploads/expense-receipts.</summary>
public sealed class LocalExpenseReceiptStorage : IExpenseReceiptStorage
{
    private readonly string _root = Path.Combine(Directory.GetCurrentDirectory(), "uploads", "expense-receipts");

    public bool IsAvailable => true;

    public async Task SaveAsync(string storageKey, Stream content, string contentType, CancellationToken ct = default)
    {
        var path = PathOf(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await content.CopyToAsync(file, ct);
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default)
    {
        var path = PathOf(storageKey);
        return Task.FromResult<Stream?>(File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true)
            : null);
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        var path = PathOf(storageKey);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string PathOf(string storageKey)
    {
        ReceiptKeys.Validate(storageKey);
        return Path.Combine(_root, storageKey.Replace('/', Path.DirectorySeparatorChar));
    }
}

/// <summary>No storage configured outside Development: uploads are refused with a message, billing still starts.</summary>
public sealed class UnavailableExpenseReceiptStorage : IExpenseReceiptStorage
{
    public bool IsAvailable => false;

    public Task SaveAsync(string storageKey, Stream content, string contentType, CancellationToken ct = default)
        => throw new InvalidOperationException("Receipt storage is not configured.");

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default) => Task.FromResult<Stream?>(null);

    public Task DeleteAsync(string storageKey, CancellationToken ct = default) => Task.CompletedTask;
}

internal static class ReceiptKeys
{
    public static void Validate(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey)
            || storageKey.StartsWith('/')
            || storageKey.Contains('\\')
            || storageKey.Split('/').Any(segment => segment is "." or ".." or ""))
        {
            throw new ArgumentException("The receipt storage key is invalid.", nameof(storageKey));
        }
    }
}

public static class ExpenseReceiptStorageServiceCollectionExtensions
{
    /// <summary>
    /// S3/MinIO when Storage:Provider says so and Storage:S3 is complete; the local folder in Development;
    /// otherwise the unavailable store. Receipts are an admin convenience, so a missing setting never
    /// stops the billing service — it turns uploads into a clear 503.
    /// </summary>
    public static IServiceCollection AddExpenseReceiptStorage(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var options = configuration.GetSection(ObjectStorageOptions.SectionName).Get<ObjectStorageOptions>() ?? new ObjectStorageOptions();
        var s3 = options.S3;
        var complete = options.UsesS3CompatibleProvider
            && Uri.TryCreate(s3.ServiceUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(s3.BucketName)
            && !string.IsNullOrWhiteSpace(s3.AccessKey)
            && !string.IsNullOrWhiteSpace(s3.SecretKey);

        if (complete)
        {
            services.AddSingleton<IExpenseReceiptStorage>(sp =>
            {
                var client = new AmazonS3Client(s3.AccessKey, s3.SecretKey, new AmazonS3Config
                {
                    ServiceURL = s3.ServiceUrl,
                    ForcePathStyle = true,
                    UseHttp = s3.ServiceUrl!.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
                });
                return new S3ExpenseReceiptStorage(client, s3);
            });
            return services;
        }

        if (environment.IsDevelopment())
        {
            services.AddSingleton<IExpenseReceiptStorage, LocalExpenseReceiptStorage>();
            return services;
        }

        services.AddSingleton<IExpenseReceiptStorage>(sp =>
        {
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("ExpenseReceiptStorage")
                .LogWarning("Storage:S3 is not configured for the billing service; expense receipt uploads are disabled.");
            return new UnavailableExpenseReceiptStorage();
        });
        return services;
    }
}

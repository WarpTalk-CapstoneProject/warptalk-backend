using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.Shared.Configuration;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Infrastructure.Storage;

/// <summary>
/// Encrypted document storage for MinIO and other S3-compatible providers.
/// The stored payload is IV + AES-256-CBC ciphertext + HMAC-SHA512 signature.
/// </summary>
public sealed class S3EncryptedWorkspaceDocumentStorage : IWorkspaceDocumentStorage
{
    private static int IvSize => WorkspaceDocumentConstants.StorageEncryption.IvSize;
    private static int SignatureSize => WorkspaceDocumentConstants.StorageEncryption.SignatureSize;

    private readonly IAmazonS3 _s3;
    private readonly ObjectStorageOptions _options;
    private readonly ILogger<S3EncryptedWorkspaceDocumentStorage> _logger;
    private readonly string _bucket;

    public S3EncryptedWorkspaceDocumentStorage(
        IAmazonS3 s3,
        IOptions<ObjectStorageOptions> options,
        ILogger<S3EncryptedWorkspaceDocumentStorage> logger)
    {
        _s3 = s3;
        _options = options.Value;
        _logger = logger;
        _bucket = _options.S3.BucketName
            ?? WorkspaceDocumentConstants.StorageEncryption.DefaultS3BucketName;
    }

    public string StorageProviderName => _options.Provider ?? StorageProviders.S3;

    public async Task<string> ReadDocumentContentAsync(
        WorkspaceDocument document,
        CancellationToken ct = default)
    {
        await using var stream = await GetDecryptedStreamAsync(document, ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    public async Task<Stream> GetDecryptedStreamAsync(
        WorkspaceDocument document,
        CancellationToken ct = default)
    {
        EnsureStorageKey(document);

        try
        {
            using var response = await _s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _bucket,
                Key = document.StorageKey
            }, ct);

            using var encrypted = new MemoryStream();
            await response.ResponseStream.CopyToAsync(encrypted, ct);
            return Decrypt(document.WorkspaceId, encrypted.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to retrieve or decrypt S3 document {DocumentId}.",
                document.Id);
            throw;
        }
    }

    public async Task SaveDocumentContentAsync(
        WorkspaceDocument document,
        Stream contentStream,
        CancellationToken ct = default)
    {
        EnsureStorageKey(document);

        if (_options.S3.EnsureBucketExists)
        {
            await EnsureBucketExistsAsync(ct);
        }

        using var payload = await EncryptAsync(document.WorkspaceId, contentStream, ct);
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = document.StorageKey,
            InputStream = payload,
            ContentType = "application/octet-stream"
        }, ct);
    }

    public async Task SaveExtractedTextAsync(
        WorkspaceDocument document,
        string text,
        CancellationToken ct = default)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await SaveDocumentContentAsync(CreateExtractedTextDocument(document), stream, ct);
    }

    public async Task<string> GetExtractedTextAsync(
        WorkspaceDocument document,
        CancellationToken ct = default)
    {
        try
        {
            await using var stream = await GetDecryptedStreamAsync(
                CreateExtractedTextDocument(document),
                ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return string.Empty;
        }
    }

    public async Task DeleteDocumentContentAsync(
        WorkspaceDocument document,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(document.StorageKey))
        {
            return;
        }

        try
        {
            await _s3.DeleteObjectAsync(_bucket, document.StorageKey, ct);
            await _s3.DeleteObjectAsync(_bucket, ExtractedTextKey(document), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to delete S3 objects for document {DocumentId}.",
                document.Id);
        }
    }

    private async Task<MemoryStream> EncryptAsync(
        Guid workspaceId,
        Stream content,
        CancellationToken ct)
    {
        var (aesKey, hmacKey) = DeriveKeys(workspaceId);
        var iv = RandomNumberGenerator.GetBytes(IvSize);

        using var ciphertextStream = new MemoryStream();
        using (var aes = Aes.Create())
        {
            aes.Key = aesKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            await using var crypto = new CryptoStream(
                ciphertextStream,
                aes.CreateEncryptor(),
                CryptoStreamMode.Write,
                leaveOpen: true);
            await content.CopyToAsync(crypto, ct);
            await crypto.FlushFinalBlockAsync(ct);
        }

        var ciphertext = ciphertextStream.ToArray();
        var signedPayload = new byte[iv.Length + ciphertext.Length];
        Buffer.BlockCopy(iv, 0, signedPayload, 0, iv.Length);
        Buffer.BlockCopy(ciphertext, 0, signedPayload, iv.Length, ciphertext.Length);

        using var hmac = new HMACSHA512(hmacKey);
        var signature = hmac.ComputeHash(signedPayload);

        var payload = new MemoryStream(signedPayload.Length + signature.Length);
        await payload.WriteAsync(signedPayload, ct);
        await payload.WriteAsync(signature, ct);
        payload.Position = 0;
        return payload;
    }

    private MemoryStream Decrypt(Guid workspaceId, byte[] payload)
    {
        if (payload.Length < IvSize + SignatureSize)
        {
            throw new InvalidDataException("Encrypted S3 payload is too short.");
        }

        var signedLength = payload.Length - SignatureSize;
        var signedPayload = payload.AsSpan(0, signedLength);
        var signature = payload.AsSpan(signedLength, SignatureSize);
        var (aesKey, hmacKey) = DeriveKeys(workspaceId);

        using (var hmac = new HMACSHA512(hmacKey))
        {
            var expected = hmac.ComputeHash(signedPayload.ToArray());
            if (!CryptographicOperations.FixedTimeEquals(signature, expected))
            {
                throw new CryptographicException("S3 document integrity validation failed.");
            }
        }

        var iv = signedPayload[..IvSize].ToArray();
        var ciphertext = signedPayload[IvSize..].ToArray();
        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        return new MemoryStream(
            decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length),
            writable: false);
    }

    private (byte[] AesKey, byte[] HmacKey) DeriveKeys(Guid workspaceId)
    {
        var masterKey = Encoding.UTF8.GetBytes(
            _options.MasterKey
            ?? throw new InvalidOperationException("Storage:MasterKey is required."));
        var workspaceSalt = workspaceId.ToByteArray();

        using var rootHmac = new HMACSHA512(masterKey);
        var rootKey = rootHmac.ComputeHash(workspaceSalt);
        using var aesHmac = new HMACSHA512(rootKey);
        using var integrityHmac = new HMACSHA512(rootKey);

        return (
            aesHmac.ComputeHash(Encoding.UTF8.GetBytes("AES"))[..32],
            integrityHmac.ComputeHash(Encoding.UTF8.GetBytes("HMAC")));
    }

    private async Task EnsureBucketExistsAsync(CancellationToken ct)
    {
        if (await AmazonS3Util.DoesS3BucketExistV2Async(_s3, _bucket))
        {
            return;
        }

        await _s3.PutBucketAsync(new PutBucketRequest
        {
            BucketName = _bucket,
            UseClientRegion = true
        }, ct);
    }

    private static WorkspaceDocument CreateExtractedTextDocument(WorkspaceDocument document) =>
        new()
        {
            Id = document.Id,
            WorkspaceId = document.WorkspaceId,
            StorageKey = ExtractedTextKey(document)
        };

    private static string ExtractedTextKey(WorkspaceDocument document) =>
        $"{document.StorageKey}_extracted.txt";

    private static void EnsureStorageKey(WorkspaceDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.StorageKey))
        {
            throw new ArgumentException("StorageKey is required.", nameof(document));
        }
    }
}

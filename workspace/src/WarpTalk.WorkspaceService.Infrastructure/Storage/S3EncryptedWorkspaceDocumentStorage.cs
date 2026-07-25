using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
/// Infrastructure implementation for MinIO / S3-compatible encrypted document storage (AES-256-CBC + HMAC-SHA512).
/// Encrypts files in memory before uploading via S3 API, and decrypts on-the-fly upon stream retrieval.
/// </summary>
public class S3EncryptedWorkspaceDocumentStorage : IWorkspaceDocumentStorage
{
    private static int IvSize => WorkspaceDocumentConstants.StorageEncryption.IvSize;
    private static int SignatureSize => WorkspaceDocumentConstants.StorageEncryption.SignatureSize;

    private readonly IAmazonS3 _s3Client;
    private readonly ILogger<S3EncryptedWorkspaceDocumentStorage> _logger;
    private readonly ObjectStorageOptions _storageOptions;
    private readonly string _bucketName;
    private readonly bool _ensureBucketExists;

    public S3EncryptedWorkspaceDocumentStorage(
        IAmazonS3 s3Client,
        IOptions<ObjectStorageOptions> storageOptions,
        ILogger<S3EncryptedWorkspaceDocumentStorage> logger)
    {
        _s3Client = s3Client;
        _storageOptions = storageOptions.Value;
        _logger = logger;
        _bucketName = _storageOptions.S3.BucketName ?? WorkspaceDocumentConstants.StorageEncryption.DefaultS3BucketName;
        _ensureBucketExists = _storageOptions.S3.EnsureBucketExists;
    }

    public async Task<string> ReadDocumentContentAsync(WorkspaceDocument document, CancellationToken ct = default)
    {
        using var stream = await GetDecryptedStreamAsync(document, ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    public async Task<Stream> GetDecryptedStreamAsync(WorkspaceDocument document, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(document.StorageKey))
        {
            throw new ArgumentException("StorageKey is null or empty.", nameof(document));
        }

        try
        {
            var getRequest = new GetObjectRequest
            {
                BucketName = _bucketName,
                Key = document.StorageKey
            };

            using var response = await _s3Client.GetObjectAsync(getRequest, ct);
            using var msEncrypted = new MemoryStream();
            await response.ResponseStream.CopyToAsync(msEncrypted, ct);
            var encryptedBytes = msEncrypted.ToArray();

            if (encryptedBytes.Length < IvSize + SignatureSize)
            {
                throw new InvalidOperationException("Encrypted payload from S3 is too short.");
            }

            var iv = new byte[IvSize];
            var signature = new byte[SignatureSize];
            var ciphertextLength = encryptedBytes.Length - IvSize - SignatureSize;
            var ciphertext = new byte[ciphertextLength];

            Array.Copy(encryptedBytes, 0, iv, 0, IvSize);
            Array.Copy(encryptedBytes, IvSize, ciphertext, 0, ciphertextLength);
            Array.Copy(encryptedBytes, IvSize + ciphertextLength, signature, 0, SignatureSize);

            var (aesKey, hmacKey) = DeriveKeys(document.WorkspaceId);

            // Step 1: Verify HMAC signature in constant time
            using (var hmac = new HMACSHA512(hmacKey))
            {
                var hmacPayload = new byte[IvSize + ciphertextLength];
                Array.Copy(iv, 0, hmacPayload, 0, IvSize);
                Array.Copy(ciphertext, 0, hmacPayload, IvSize, ciphertextLength);

                var computedSignature = hmac.ComputeHash(hmacPayload);
                if (!CryptographicEquals(signature, computedSignature))
                {
                    throw new CryptographicException("Integrity check failed: HMAC signature mismatch for S3 object.");
                }
            }

            // Step 2: Decrypt ciphertext using AES-256-CBC
            using (var aes = Aes.Create())
            {
                aes.Key = aesKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using var decryptor = aes.CreateDecryptor();
                var decryptedBytes = decryptor.TransformFinalBlock(ciphertext, 0, ciphertextLength);
                return new MemoryStream(decryptedBytes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve or decrypt document {DocumentId} from MinIO S3.", document.Id);
            throw;
        }
    }

    public async Task SaveDocumentContentAsync(WorkspaceDocument document, Stream contentStream, CancellationToken ct = default)
    {
        var (aesKey, hmacKey) = DeriveKeys(document.WorkspaceId);

        byte[] iv = new byte[IvSize];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(iv);
        }

        using var msCipher = new MemoryStream();
        using (var aes = Aes.Create())
        {
            aes.Key = aesKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using (var encryptor = aes.CreateEncryptor())
            using (var cryptoStream = new CryptoStream(msCipher, encryptor, CryptoStreamMode.Write))
            {
                await contentStream.CopyToAsync(cryptoStream, ct);
                await cryptoStream.FlushFinalBlockAsync(ct);
            }
        }

        var ciphertext = msCipher.ToArray();

        byte[] signature;
        using (var hmac = new HMACSHA512(hmacKey))
        {
            var hmacPayload = new byte[iv.Length + ciphertext.Length];
            Array.Copy(iv, 0, hmacPayload, 0, iv.Length);
            Array.Copy(ciphertext, 0, hmacPayload, iv.Length, ciphertext.Length);
            signature = hmac.ComputeHash(hmacPayload);
        }

        using var msPayload = new MemoryStream();
        await msPayload.WriteAsync(iv, 0, iv.Length, ct);
        await msPayload.WriteAsync(ciphertext, 0, ciphertext.Length, ct);
        await msPayload.WriteAsync(signature, 0, signature.Length, ct);
        msPayload.Position = 0;

        if (_ensureBucketExists)
        {
            await EnsureBucketExistsAsync(ct);
        }

        var putRequest = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = document.StorageKey,
            InputStream = msPayload,
            ContentType = "application/octet-stream"
        };

        await _s3Client.PutObjectAsync(putRequest, ct);
    }

    public async Task SaveExtractedTextAsync(WorkspaceDocument document, string text, CancellationToken ct = default)
    {
        var rawBytes = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream(rawBytes);

        var textDocument = new WorkspaceDocument
        {
            Id = document.Id,
            WorkspaceId = document.WorkspaceId,
            StorageKey = document.StorageKey + "_extracted.txt"
        };

        await SaveDocumentContentAsync(textDocument, ms, ct);
    }

    public async Task<string> GetExtractedTextAsync(WorkspaceDocument document, CancellationToken ct = default)
    {
        var textDocument = new WorkspaceDocument
        {
            Id = document.Id,
            WorkspaceId = document.WorkspaceId,
            StorageKey = document.StorageKey + "_extracted.txt"
        };

        try
        {
            using var stream = await GetDecryptedStreamAsync(textDocument, ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public async Task DeleteDocumentContentAsync(WorkspaceDocument document, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(document.StorageKey)) return;

        try
        {
            var deleteRequest = new DeleteObjectRequest
            {
                BucketName = _bucketName,
                Key = document.StorageKey
            };
            await _s3Client.DeleteObjectAsync(deleteRequest, ct);

            var deleteExtractedRequest = new DeleteObjectRequest
            {
                BucketName = _bucketName,
                Key = document.StorageKey + "_extracted.txt"
            };
            await _s3Client.DeleteObjectAsync(deleteExtractedRequest, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete S3 object {StorageKey}", document.StorageKey);
        }
    }

    private async Task EnsureBucketExistsAsync(CancellationToken ct)
    {
        try
        {
            var exists = await AmazonS3Util.DoesS3BucketExistV2Async(_s3Client, _bucketName);
            if (!exists)
            {
                var putBucketRequest = new PutBucketRequest
                {
                    BucketName = _bucketName,
                    UseClientRegion = true
                };
                await _s3Client.PutBucketAsync(putBucketRequest, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EnsureBucketExistsAsync warning for bucket {BucketName}", _bucketName);
        }
    }

    private (byte[] AesKey, byte[] HmacKey) DeriveKeys(Guid workspaceId)
    {
        var masterKeyStr = _storageOptions.MasterKey ?? WorkspaceDocumentConstants.StorageEncryption.DefaultMasterKeyFallback;
        var masterKey = Encoding.UTF8.GetBytes(masterKeyStr);
        var salt = workspaceId.ToByteArray();

        using var hkdf = new HMACSHA256(masterKey);
        var prk = hkdf.ComputeHash(salt);

        using var hmacAes = new HMACSHA256(prk);
        var aesKey = hmacAes.ComputeHash(Encoding.UTF8.GetBytes("AES-256-Key-Derivation"));

        using var hmacSign = new HMACSHA256(prk);
        var hmacKey = hmacSign.ComputeHash(Encoding.UTF8.GetBytes("HMAC-SHA512-Key-Derivation"));

        return (aesKey, hmacKey);
    }

    private static bool CryptographicEquals(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        int result = 0;
        for (int i = 0; i < a.Length; i++)
        {
            result |= a[i] ^ b[i];
        }
        return result == 0;
    }
}

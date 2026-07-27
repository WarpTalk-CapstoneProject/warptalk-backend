using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.Shared.Configuration;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Constants;
using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Infrastructure.Storage;

/// <summary>
/// Infrastructure implementation for local encrypted document storage (AES-256-CBC + HMAC-SHA512).
/// Includes constant-time HMAC validation to prevent timing side-channel attacks.
/// </summary>
public class LocalEncryptedWorkspaceDocumentStorage : IWorkspaceDocumentStorage
{
    private static int IvSize => WorkspaceDocumentConstants.StorageEncryption.IvSize;
    private static int SignatureSize => WorkspaceDocumentConstants.StorageEncryption.SignatureSize;

    private readonly ObjectStorageOptions _storageOptions;
    private readonly ILogger<LocalEncryptedWorkspaceDocumentStorage> _logger;

    public LocalEncryptedWorkspaceDocumentStorage(
        IOptions<ObjectStorageOptions> storageOptions,
        ILogger<LocalEncryptedWorkspaceDocumentStorage> logger)
    {
        _storageOptions = storageOptions.Value;
        _logger = logger;
    }

    public string StorageProviderName => _storageOptions.Provider ?? StorageProviders.Local;

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

        var fullPath = GetFullPath(document.StorageKey);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Encrypted document file not found at {fullPath}", document.StorageKey);
        }

        try
        {
            var encryptedBytes = await File.ReadAllBytesAsync(fullPath, ct);

            if (encryptedBytes.Length < IvSize + SignatureSize)
            {
                throw new InvalidOperationException("Encrypted file payload is too short.");
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
                    throw new CryptographicException("Integrity check failed: HMAC signature mismatch.");
                }
            }

            // Step 2: Decrypt ciphertext using AES-256-CBC
            using (var aes = Aes.Create())
            {
                aes.Key = aesKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var decryptor = aes.CreateDecryptor())
                {
                    var decryptedBytes = decryptor.TransformFinalBlock(ciphertext, 0, ciphertextLength);
                    return new MemoryStream(decryptedBytes);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt document {DocumentId}.", document.Id);
            throw;
        }
    }

    public async Task SaveDocumentContentAsync(WorkspaceDocument document, Stream contentStream, CancellationToken ct = default)
    {
        var baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "uploads");
        if (!Directory.Exists(baseDir))
        {
            baseDir = Path.Combine(Directory.GetCurrentDirectory(), "uploads");
        }
        var fullPath = Path.Combine(baseDir, document.StorageKey);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var (aesKey, hmacKey) = DeriveKeys(document.WorkspaceId);

        // Step 1: Generate Cryptographically Secure Random IV (IvSize bytes)
        byte[] iv = new byte[IvSize];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(iv);
        }

        // Step 2: Encrypt contentStream to MemoryStream
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

        // Step 3: Compute HMAC-SHA512 over [IV + Ciphertext]
        byte[] signature;
        using (var hmac = new HMACSHA512(hmacKey))
        {
            var hmacPayload = new byte[iv.Length + ciphertext.Length];
            Array.Copy(iv, 0, hmacPayload, 0, iv.Length);
            Array.Copy(ciphertext, 0, hmacPayload, iv.Length, ciphertext.Length);
            signature = hmac.ComputeHash(hmacPayload);
        }

        // Step 4: Write binary layout to file: [IV (16 bytes)] [Ciphertext] [HMAC Signature (64 bytes)]
        using (var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await fileStream.WriteAsync(iv, 0, iv.Length, ct);
            await fileStream.WriteAsync(ciphertext, 0, ciphertext.Length, ct);
            await fileStream.WriteAsync(signature, 0, signature.Length, ct);
        }
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
        catch (FileNotFoundException)
        {
            return string.Empty;
        }
    }

    public Task DeleteDocumentContentAsync(WorkspaceDocument document, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(document.StorageKey))
        {
            return Task.CompletedTask;
        }

        var fullPath = GetFullPath(document.StorageKey);

        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception ex)
        {
            // Best-effort cleanup — log but don't let this mask the caller's original failure.
            _logger.LogError(ex, "Failed to delete orphaned document blob at {Path} for document {DocumentId}.", fullPath, document.Id);
        }

        return Task.CompletedTask;
    }

    private (byte[] AesKey, byte[] HmacKey) DeriveKeys(Guid workspaceId)
    {
        var masterKeyStr = _storageOptions.MasterKey ?? "CHANGE_ME_SUPER_SECRET_STORAGE_MASTER_KEY_MIN_32_CHARS!!";
        var masterKeyBytes = Encoding.UTF8.GetBytes(masterKeyStr);

        // Key Derivation using HMAC-SHA512
        using var hmacAes = new HMACSHA512(masterKeyBytes);
        var aesPayload = workspaceId.ToByteArray().Concat(Encoding.UTF8.GetBytes("AES")).ToArray();
        var aesDerived = hmacAes.ComputeHash(aesPayload);

        using var hmacMac = new HMACSHA512(masterKeyBytes);
        var macPayload = workspaceId.ToByteArray().Concat(Encoding.UTF8.GetBytes("HMAC")).ToArray();
        var macDerived = hmacMac.ComputeHash(macPayload);

        var aesKey = new byte[32];
        var hmacKey = new byte[64];

        Array.Copy(aesDerived, 0, aesKey, 0, 32);
        Array.Copy(macDerived, 0, hmacKey, 0, 64);

        return (aesKey, hmacKey);
    }

    private string GetFullPath(string storageKey)
    {
        var baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "uploads");
        if (!Directory.Exists(baseDir))
        {
            baseDir = Path.Combine(Directory.GetCurrentDirectory(), "uploads");
        }
        return Path.Combine(baseDir, storageKey);
    }

    private bool FileExists(string storageKey)
    {
        return File.Exists(GetFullPath(storageKey));
    }

    private static bool CryptographicEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int result = 0;
        for (int i = 0; i < a.Length; i++)
        {
            result |= a[i] ^ b[i];
        }
        return result == 0;
    }
}

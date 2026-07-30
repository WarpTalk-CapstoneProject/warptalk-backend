using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.Interfaces;

namespace WarpTalk.AuthService.Infrastructure.Storage;

public class LocalVoiceSampleStorage : IVoiceSampleStorage
{
    public async Task<string> SaveAsync(string storageKey, Stream contentStream, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await contentStream.CopyToAsync(fileStream, ct);
        return storageKey;
    }

    public Task<Stream> ReadAsync(string storageKey, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(storageKey);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Voice sample file not found at {fullPath}", storageKey);
        }

        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(storageKey);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
        return Task.CompletedTask;
    }

    private static string GetFullPath(string storageKey)
    {
        var baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "uploads", "voice-samples");
        if (!Directory.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "uploads")))
        {
            baseDir = Path.Combine(Directory.GetCurrentDirectory(), "uploads", "voice-samples");
        }
        return Path.Combine(baseDir, storageKey);
    }
}

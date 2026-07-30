using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.MeetingService.Application.Interfaces;

namespace WarpTalk.MeetingService.Infrastructure.Storage;

// Local-disk storage, same technique as AuthService's LocalVoiceSampleStorage — a
// storage key is saved under uploads/ and reopened by that same key on read/delete.
public class LocalMeetingChatFileStorage : IMeetingChatFileStorage
{
    public async Task SaveAsync(string storageKey, Stream contentStream, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await contentStream.CopyToAsync(fileStream, ct);
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(storageKey);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Chat file not found at {fullPath}", storageKey);
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
        var baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "uploads", "meeting-chat-files");
        if (!Directory.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "uploads")))
        {
            baseDir = Path.Combine(Directory.GetCurrentDirectory(), "uploads", "meeting-chat-files");
        }
        return Path.Combine(baseDir, storageKey);
    }
}

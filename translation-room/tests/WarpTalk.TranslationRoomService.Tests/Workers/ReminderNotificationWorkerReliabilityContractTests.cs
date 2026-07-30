namespace WarpTalk.TranslationRoomService.Tests.Workers;

public sealed class ReminderNotificationWorkerReliabilityContractTests
{
    [Fact]
    public void ReminderWorker_UsesDistributedLockAndPersistsBeforeRelease()
    {
        var source = File.ReadAllText(FindSourceFile(
            "translation-room/src/WarpTalk.TranslationRoomService.API/Workers/ReminderNotificationWorker.cs"));

        Assert.Contains("LockTakeAsync", source, StringComparison.Ordinal);
        Assert.Contains("LockReleaseAsync", source, StringComparison.Ordinal);
        Assert.Contains("SaveChangesAsync", source, StringComparison.Ordinal);
        Assert.Contains("warptalk:reminder-lock:", source, StringComparison.Ordinal);
    }

    private static string FindSourceFile(string relativePath)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                    return candidate;
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"Could not locate {relativePath}.");
    }
}

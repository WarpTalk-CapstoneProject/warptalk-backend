namespace WarpTalk.TranslationRoomService.Tests.Workers;

public sealed class IdleRoomMonitoringWorkerPresenceContractTests
{
    [Fact]
    public void IdleWorker_TreatsJoinedAndConnectedParticipantsAsPresent()
    {
        var source = File.ReadAllText(FindSourceFile(
            "translation-room/src/WarpTalk.TranslationRoomService.API/Workers/IdleRoomMonitoringWorker.cs"));

        Assert.Contains(
            "p.Status == \"CONNECTED\" || p.Status == \"JOINED\"",
            source,
            StringComparison.Ordinal);
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

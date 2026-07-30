namespace WarpTalk.TranslationRoomService.Tests.Workers;

public sealed class ArtifactsFinalizationWorkerLifecycleContractTests
{
    [Fact]
    public void ArtifactsFinalizationWorker_DoesNotFanOutUnboundedUnobservedTasks()
    {
        var source = File.ReadAllText(FindSourceFile(
            "translation-room/src/WarpTalk.TranslationRoomService.API/Workers/ArtifactsFinalizationWorker.cs"));

        Assert.DoesNotContain("Task.Run(", source, StringComparison.Ordinal);
        Assert.Contains("SemaphoreSlim", source, StringComparison.Ordinal);
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

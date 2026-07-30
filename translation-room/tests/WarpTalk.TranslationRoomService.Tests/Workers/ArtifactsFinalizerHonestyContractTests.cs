namespace WarpTalk.TranslationRoomService.Tests.Workers;

public sealed class ArtifactsFinalizerHonestyContractTests
{
    [Fact]
    public void SummaryFallback_DoesNotClaimSuccessfulProcessingWhenDataIsMissing()
    {
        var source = File.ReadAllText(FindSourceFile(
            "translation-room/src/WarpTalk.TranslationRoomService.Infrastructure/BackgroundProcessors/ArtifactsFinalizer.cs"));

        Assert.DoesNotContain("All system processes completed successfully", source, StringComparison.Ordinal);
        Assert.Contains("insufficientData = true", source, StringComparison.Ordinal);
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

namespace WarpTalk.TranslationRoomService.Tests.Workers;

public sealed class ArtifactsFinalizerHonestyContractTests
{
    [Fact]
    public void SummaryFallback_DoesNotClaimSuccessfulProcessingWhenDataIsMissing()
    {
        var finalizer = File.ReadAllText(FindSourceFile(
            "translation-room/src/WarpTalk.TranslationRoomService.Infrastructure/BackgroundProcessors/ArtifactsFinalizer.cs"));

        Assert.DoesNotContain("All system processes completed successfully", finalizer, StringComparison.Ordinal);

        // WT-379 moved the JSON shaping into SummaryContentBuilder so the finalizer and the
        // late-summary recovery in ArtifactsReconciliationWorker cannot drift apart. The
        // invariant this test defends did not change — a summary that was never generated must
        // say so — only where it lives. This assertion follows it rather than being deleted,
        // because deleting it is how the honest fallback quietly becomes an optimistic one.
        var builder = File.ReadAllText(FindSourceFile(
            "translation-room/src/WarpTalk.TranslationRoomService.Application/Helpers/SummaryContentBuilder.cs"));

        Assert.DoesNotContain("All system processes completed successfully", builder, StringComparison.Ordinal);
        Assert.Contains("insufficientData = true", builder, StringComparison.Ordinal);
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

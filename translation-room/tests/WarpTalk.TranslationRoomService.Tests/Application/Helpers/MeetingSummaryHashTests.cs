using WarpTalk.TranslationRoomService.Application.Helpers;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

public sealed class MeetingSummaryHashTests
{
    /// <summary>
    /// The hash exactly as ai_assistant_worker/worker.py leaves it: three hset calls, on
    /// "content", "action_items" and "structured_json". Pinned here because there is nothing on a
    /// Redis hash to compile against — if these names ever stop matching that worker, this is the
    /// test that says so rather than a meeting quietly losing its summary.
    /// </summary>
    [Fact]
    public void Read_ReadsTheFieldsTheAiWorkerActuallyWrites()
    {
        var hash = new Dictionary<string, string>
        {
            ["content"] = "The team agreed to ship on Friday.",
            ["action_items"] = "- Nhi: cut the release branch",
            ["structured_json"] = "{\"summary\":\"Ship Friday\"}",
        };

        var (content, actionItems, structuredJson) =
            MeetingSummaryHash.Read(name => hash.GetValueOrDefault(name));

        Assert.Equal("The team agreed to ship on Friday.", content);
        Assert.Equal("- Nhi: cut the release branch", actionItems);
        Assert.Equal("{\"summary\":\"Ship Friday\"}", structuredJson);
        Assert.True(MeetingSummaryHash.HasAnything(content, actionItems, structuredJson));
    }

    /// <summary>
    /// The names the late-summary recovery used to ask for. Nothing writes them, and a reader
    /// spelling them this way finds an empty summary in a hash that is full.
    /// </summary>
    [Fact]
    public void Read_FindsNothingUnderTheNamesNobodyWrites()
    {
        var hash = new Dictionary<string, string>
        {
            ["summary"] = "The team agreed to ship on Friday.",
            ["structured"] = "{\"summary\":\"Ship Friday\"}",
        };

        var (content, actionItems, structuredJson) =
            MeetingSummaryHash.Read(name => hash.GetValueOrDefault(name));

        Assert.Null(content);
        Assert.Null(actionItems);
        Assert.Null(structuredJson);
        Assert.False(MeetingSummaryHash.HasAnything(content, actionItems, structuredJson));
    }

    /// <summary>
    /// One field is enough. The three are written by three separate hset calls with an LLM round
    /// trip between them, so a hash holding only the prose summary is one being written, not an
    /// empty one — and treating it as empty is how a recovery skips the summary it came for.
    /// </summary>
    [Fact]
    public void HasAnything_IsTrueForAHashThatIsStillBeingFilled()
    {
        Assert.True(MeetingSummaryHash.HasAnything("A summary.", null, null));
        Assert.True(MeetingSummaryHash.HasAnything(null, null, "{\"summary\":\"x\"}"));
        Assert.False(MeetingSummaryHash.HasAnything(null, "   ", string.Empty));
    }

    /// <summary>
    /// Both readers of meeting:{roomId}:summary go through the constants above, and neither
    /// spells a field name of its own.
    ///
    /// This is the assertion that fails if the drift comes back. ArtifactsReconciliationWorker
    /// read "structured" and "summary" — names ai_assistant_worker has never written — while
    /// ArtifactsFinalizer, ten files away, read the right ones. Nothing failed to compile and
    /// nothing failed at runtime; the recovery simply never recovered anything, and on a meeting
    /// with action items it rebuilt the artifact from those alone and deleted the key.
    /// </summary>
    [Fact]
    public void BothReadersOfTheSummaryHashGoThroughTheseConstants()
    {
        var recovery = File.ReadAllText(FindSourceFile(
            "translation-room/src/WarpTalk.TranslationRoomService.API/Workers/ArtifactsReconciliationWorker.cs"));

        Assert.Contains("MeetingSummaryHash.Read", recovery, StringComparison.Ordinal);
        // The local Field(...) accessor may still exist; being HANDED a name is the defect.
        Assert.DoesNotContain("Field(\"", recovery, StringComparison.Ordinal);

        var finalizer = File.ReadAllText(FindSourceFile(
            "translation-room/src/WarpTalk.TranslationRoomService.Infrastructure/BackgroundProcessors/ArtifactsFinalizer.cs"));

        Assert.Contains("MeetingSummaryHash.Content", finalizer, StringComparison.Ordinal);
        Assert.DoesNotContain("HashGetAsync(summaryKey, \"", finalizer, StringComparison.Ordinal);
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

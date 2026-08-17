using System.IO;
using System.Linq;

namespace WarpTalk.TranslationRoomService.Tests.Contracts;

/// <summary>
/// WT-431/WT-432. Two claims the finalizer must not make.
///
/// It must not say a meeting was silent when it could not read the transcript, and it must not
/// label the summary payload markdown when it stores JSON. Both were true of every artifact in
/// production, and both are the kind of defect that leaves the system looking healthy: the row is
/// written, the status is COMPLETED, the deploy is green, and the content is a confident lie.
///
/// Asserted against the source rather than by driving the finalizer, because what matters is the
/// SHAPE of the code — that the failure branch and the empty-result branch cannot converge on one
/// sentence again, which is precisely what a behavioural test over a mocked client would not
/// notice.
/// </summary>
public sealed class TranscriptArtifactHonestyContractTests
{
    private static string Finalizer() => CodeOnly(File.ReadAllText(Path.Combine(
        FindBackendRoot(),
        "translation-room/src/WarpTalk.TranslationRoomService.Infrastructure/BackgroundProcessors/ArtifactsFinalizer.cs")));

    /// <summary>
    /// The source with comments removed.
    ///
    /// These assertions are about what the code DOES, and a comment explaining a fix names the
    /// very strings the fix removed — the WT-431 note in the catch block quotes the silence
    /// sentence in order to explain why it must not be printed there. Scanning raw text made
    /// documenting the bug indistinguishable from committing it, which would have taught the next
    /// person to delete the explanation to get the build green.
    /// </summary>
    private static string CodeOnly(string source)
    {
        var output = new System.Text.StringBuilder(source.Length);
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            if (c == '"' || c == '\'')
            {
                // A verbatim string can hold a newline and doubles its quotes to escape them.
                var verbatim = i > 0 && source[i - 1] == '@';
                var quote = c;
                output.Append(c);
                i++;

                while (i < source.Length)
                {
                    if (!verbatim && source[i] == '\\' && i + 1 < source.Length)
                    {
                        output.Append(source[i]).Append(source[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (source[i] == quote)
                    {
                        if (verbatim && i + 1 < source.Length && source[i + 1] == quote)
                        {
                            output.Append(quote).Append(quote);
                            i += 2;
                            continue;
                        }
                        output.Append(quote);
                        i++;
                        break;
                    }
                    output.Append(source[i]);
                    i++;
                }
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i += 2;
                continue;
            }

            output.Append(c);
            i++;
        }

        return output.ToString();
    }

    [Fact]
    public void TheSilenceSentenceIsReachableOnlyWhenTheTranscriptWasActuallyRead()
    {
        var source = Finalizer();

        // The one place that may claim silence is the formatter fed by a SUCCESSFUL read.
        var occurrences = source.Split("No speech transcription recorded").Length - 1;
        Assert.True(
            occurrences == 1,
            $"'No speech transcription recorded' appears {occurrences} times; it must exist only in "
            + "FormatTranscriptText, which is reached only after the transcript service answered.");

        Assert.Contains("FormatUnavailableTranscriptText", source, StringComparison.Ordinal);
        Assert.Contains("not** a statement that nobody spoke", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeadCacheFallbackCannotSpeakForAFailure()
    {
        var source = Finalizer();

        // AssembleTranscriptAsync returned a finished document — header plus the silence sentence
        // when the list was empty — so the caller could not tell an empty cache from a silent
        // meeting. Reading raw lines is what makes that distinction possible at all.
        Assert.DoesNotContain("AssembleTranscriptAsync", source, StringComparison.Ordinal);
        Assert.Contains("ReadCachedSegmentsAsync", source, StringComparison.Ordinal);
        Assert.Contains("cachedSegments.Count > 0", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtifactSizeIsMeasuredFromTheContentThatIsStored()
    {
        var source = Finalizer();

        // The bug: FormatSummaryText's output was measured, then discarded, and a different string
        // was stored. Both builders now take the content itself and measure that one string.
        Assert.DoesNotContain("FormatSummaryText", source, StringComparison.Ordinal);

        foreach (var builder in new[] { "BuildTranscriptArtifact", "BuildSummaryArtifact" })
        {
            var body = MethodBody(source, $"private static TranslationRoomArtifact {builder}(Guid roomId, string content)");
            Assert.Contains("Encoding.UTF8.GetByteCount(content)", body, StringComparison.Ordinal);
            Assert.Contains("content: content", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheSummaryIsLabelledJsonAndTheTranscriptMarkdown()
    {
        var source = Finalizer();

        Assert.Contains("ArtifactFileFormats.Json", source, StringComparison.Ordinal);
        Assert.Contains("ArtifactFileFormats.Markdown", source, StringComparison.Ordinal);

        // A MIME string here is what made the download switch fall through to .txt for every
        // artifact ever produced. FileFormat is a token.
        Assert.DoesNotContain("\"text/markdown\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDownloadPathStillResolvesTheRowsAlreadyInProduction()
    {
        // 135 rows say "text/markdown". Dropping that from the switch would silently regress them
        // from .md to .txt while the fix was being celebrated.
        var download = CodeOnly(File.ReadAllText(Path.Combine(
            FindBackendRoot(),
            "translation-room/src/WarpTalk.TranslationRoomService.Application/Services/TranslationRoomArtifactService.cs")));

        Assert.Contains("ArtifactFileFormats.LegacyMarkdownMime", download, StringComparison.Ordinal);
    }

    /// <summary>Everything from a signature to the line that closes it, brace-counted.</summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find `{signature}`.");

        var end = source.IndexOf(';', start);
        Assert.True(end > start, $"Could not find the end of `{signature}`.");
        return source[start..end];
    }

    private static string FindBackendRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "warptalk-backend.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate backend repository root.");
    }
}

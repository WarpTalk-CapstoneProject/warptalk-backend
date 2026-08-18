namespace WarpTalk.TranslationRoomService.Domain.Constants;

/// <summary>
/// The vocabulary of <c>TranslationRoomArtifact.FileFormat</c>.
///
/// WT-432: these are TOKENS, not MIME types. TranslationRoomArtifactService.GetArtifactDownloadAsync
/// switches on this column to pick a file extension and a Content-Type, and it matches "markdown"
/// and "json" — so a writer that stored the MIME string "text/markdown" fell through to the
/// default and served the file as .txt/text-plain. Every artifact in production did.
///
/// A constant exists so the writer and the switch cannot drift again: the finalizer, the
/// reconciliation worker and the download path all name the same symbol.
/// </summary>
public static class ArtifactFileFormats
{
    /// <summary>A markdown document — the transcript export.</summary>
    public const string Markdown = "markdown";

    /// <summary>
    /// Structured JSON — the summary export. The frontend parses this with
    /// parseMeetingSummaryContent; it is deliberately not markdown.
    /// </summary>
    public const string Json = "json";

    /// <summary>Plain text.</summary>
    public const string PlainText = "text/plain";

    /// <summary>
    /// What the finalizer wrote before WT-432: a MIME type where a token belonged. Still read on
    /// the download path so the 135 rows already in production keep resolving to markdown instead
    /// of falling through to the .txt default. Nothing should WRITE this.
    /// </summary>
    public const string LegacyMarkdownMime = "text/markdown";
}

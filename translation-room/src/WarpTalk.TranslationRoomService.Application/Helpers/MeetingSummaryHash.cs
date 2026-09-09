using System;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// The field names inside <c>meeting:{roomId}:summary</c>, in one place.
///
/// WHY THIS EXISTS
///     Two readers in this service read that hash: <c>ArtifactsFinalizer.WaitForSummaryAsync</c>
///     while the meeting is being finalized, and
///     <c>ArtifactsReconciliationWorker.RecoverLateSummariesAsync</c> when the summary arrived
///     after the finalizer stopped waiting (WT-379). They spelled the fields differently, and
///     only one of them matched the writer.
///
///     ai_assistant_worker/worker.py writes three fields and no others — <c>content</c> (the
///     prose summary), <c>action_items</c>, and <c>structured_json</c> (see
///     <c>_generate_summary</c>, one <c>hset</c> per field). The recovery read
///     <c>summary</c> and <c>structured</c>, which nothing has ever written. So the recovery it
///     exists to perform could not fire on the prose summary at all; and when the meeting had
///     action items — the one name that did match — it rebuilt the artifact from those ALONE and
///     then deleted the key, destroying the summary it was recovering. Either way the meeting
///     page kept saying the summary never arrived while the summary sat in Redis.
///
///     There is nothing to compile against on a Redis hash, so a shared constant is the only
///     thing that can make the two readers wrong together rather than separately. Renaming a
///     field means changing it here and in ai_assistant_worker in the same release.
/// </summary>
public static class MeetingSummaryHash
{
    /// <summary>The prose summary. <c>hset(..., "content", summary)</c>.</summary>
    public const string Content = "content";

    /// <summary>The extracted action items, as text. <c>hset(..., "action_items", ...)</c>.</summary>
    public const string ActionItems = "action_items";

    /// <summary>
    /// The <c>{summary, decisions[], actionItems[]}</c> JSON from
    /// <c>MeetingAssistant.generate_structured_summary</c>. <c>hset(..., "structured_json", ...)</c>.
    /// </summary>
    public const string StructuredJson = "structured_json";

    /// <summary>
    /// Reads all three fields through whatever the caller's Redis client hands back — a hash
    /// lookup here, individual HGETs there — so the NAMES are shared even though the two readers
    /// fetch them differently.
    /// </summary>
    public static (string? Content, string? ActionItems, string? StructuredJson) Read(
        Func<string, string?> field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return (field(Content), field(ActionItems), field(StructuredJson));
    }

    /// <summary>
    /// Whether the worker has written anything worth building an artifact out of.
    ///
    /// Any ONE field is enough: the three are written by three separate <c>hset</c> calls with an
    /// LLM round trip between them, so a hash holding only <c>content</c> is a summary that is
    /// still being written, not an empty one.
    /// </summary>
    public static bool HasAnything(string? content, string? actionItems, string? structuredJson) =>
        !string.IsNullOrWhiteSpace(content)
        || !string.IsNullOrWhiteSpace(actionItems)
        || !string.IsNullOrWhiteSpace(structuredJson);
}

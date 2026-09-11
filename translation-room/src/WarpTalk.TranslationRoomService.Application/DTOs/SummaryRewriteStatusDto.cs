namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// What became of one summary rewrite, for the person who asked for it.
///
/// WHY THIS EXISTS AT ALL
///     Rewriting is queued: the endpoint answers 202 and the summary lands later, over the
///     artifacts the browser refetches. That is fine when it works. When it does not, the
///     reason was written by warptalk-ai, read by SummaryResultConsumerWorker, logged, and
///     dropped — so a transcript that could not be read, a model call that threw, and a
///     request that never reached a worker all showed the requester the same thing: a panel
///     that did not change, and ninety seconds later a sentence that named none of them.
/// </summary>
public class SummaryRewriteStatusDto
{
    /// <summary>
    /// <c>pending</c>, <c>completed</c> or <c>failed</c>.
    ///
    /// <c>pending</c> covers two situations on purpose, because nothing here can tell them
    /// apart and guessing would be worse than waiting: the rewrite is genuinely still running,
    /// and the outcome expired out of Redis before anybody asked. Both mean "no answer yet",
    /// and the client already has its own deadline for that.
    /// </summary>
    public string Status { get; set; } = "pending";

    /// <summary>
    /// The reason, in the words the worker wrote — "This meeting has no saved transcript to
    /// summarise", "Could not read the transcript". Present only when <see cref="Status"/> is
    /// <c>failed</c>, and meant to be shown, not just logged.
    /// </summary>
    public string? Error { get; set; }
}

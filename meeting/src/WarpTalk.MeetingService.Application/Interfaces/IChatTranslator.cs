using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

/// <summary>
/// Translates a single chat message on demand. Implementations must return clean
/// output — the translated text only, no quotes/notes/original — since it is shown
/// directly in the chat UI with no further post-processing.
/// </summary>
public interface IChatTranslator
{
    /// <summary>Model identifier persisted alongside translations (e.g. "gpt-4o-mini").</summary>
    string ModelName { get; }

    /// <summary>
    /// Bump this when the translation prompt or model changes meaningfully. It is folded
    /// into the cache key (message_id, target_language, prompt_version), so a bump makes
    /// every previously cached translation a miss — retranslated under the new prompt —
    /// without deleting the old rows (kept for history/comparison).
    /// </summary>
    int PromptVersion { get; }

    Task<Result<string>> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken ct = default);
}

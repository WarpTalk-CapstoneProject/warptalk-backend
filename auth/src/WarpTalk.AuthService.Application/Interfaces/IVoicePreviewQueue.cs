using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.AuthService.Application.Interfaces;

/// <summary>One rendered sample, or the reason there is not one.</summary>
/// <param name="Audio">WAV bytes, when it worked.</param>
/// <param name="Error">Why it did not, when it did not. Never both this and Audio.</param>
public sealed record VoicePreview(byte[]? Audio, string? Error);

/// <summary>
/// Asks the AI side for a sample of one voice, so somebody can hear a voice before a meeting
/// rather than discovering it during one.
///
/// WHY THIS IS A QUEUE AND NOT A METHOD CALL
///     The same boundary <see cref="IVoiceCloneRequestQueue"/> exists to cross, for the same
///     reason: synthesis needs the Cartesia API key, and this service deliberately does not hold
///     one. It can offer the button; only the AI side can produce the audio behind it.
///
/// WHY THIS ONE WAITS AND THE CLONE QUEUE DOES NOT
///     A clone is collected lazily because nobody is looking at it — the answer is wanted the
///     next time the page is opened, minutes or days later. A preview is a person with their
///     finger still on a play button, so the request path waits for it.
///
/// WHY WAITING IS A POLL AND NOT A SUBSCRIPTION
///     StackExchange.Redis multiplexes one connection across the whole process, so a blocking
///     pop would hold a shared connection hostage for every other caller. Polling a key costs a
///     few round trips on a request that is already waiting on a synthesis.
/// </summary>
public interface IVoicePreviewQueue
{
    /// <summary>
    /// A previously rendered sample, if one is already waiting. Null means "not rendered yet",
    /// which is not an error — it is the ordinary state the first time anybody asks for a voice.
    ///
    /// Worth calling on its own before <see cref="RequestAsync"/>: the AI side keys the result by
    /// (voice, language) rather than by request, so every play after the first is a cache read
    /// and costs no synthesis at all.
    /// </summary>
    Task<VoicePreview?> TryGetAsync(string voiceId, string language, CancellationToken ct = default);

    /// <summary>
    /// Ask for this voice to be rendered. False means the request could not be queued — Redis is
    /// down — and the caller must say the preview is unavailable rather than wait for an answer
    /// that was never asked for.
    /// </summary>
    Task<bool> RequestAsync(string voiceId, string language, CancellationToken ct = default);

    /// <summary>
    /// Wait up to <see cref="RenderTimeout"/> for the answer to appear. Null means it did not
    /// arrive in time, which is a real outcome rather than a failure: the render may still land
    /// and be served instantly to the next person who presses play.
    /// </summary>
    Task<VoicePreview?> WaitAsync(string voiceId, string language, CancellationToken ct = default);

    /// <summary>
    /// How long a play button may hang before it is honest about not having audio.
    ///
    /// A Cartesia render of one short sentence comes back well inside this; the budget is for a
    /// worker that is busy or mid-restart, not for a slow synthesis. Longer would keep somebody
    /// staring at a spinner for a result that is not coming, and the retry costs them nothing
    /// because a late render is cached and served instantly.
    /// </summary>
    static TimeSpan RenderTimeout => TimeSpan.FromSeconds(12);
}

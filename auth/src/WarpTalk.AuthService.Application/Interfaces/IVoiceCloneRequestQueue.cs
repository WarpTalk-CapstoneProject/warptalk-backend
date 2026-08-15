using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.AuthService.Application.Interfaces;

/// <summary>The answer the AI side left for one clone request.</summary>
/// <param name="VoiceId">The provider voice id, when it worked.</param>
/// <param name="Provider">"cartesia".</param>
/// <param name="Error">Why it did not, when it did not. Never both this and VoiceId.</param>
public sealed record VoiceCloneOutcome(string? VoiceId, string? Provider, string? Error);

/// <summary>
/// Asks the AI side to turn an uploaded recording into a provider voice, and collects the answer.
///
/// WHY THIS IS A QUEUE AND NOT A METHOD CALL
///     Cloning needs the Cartesia API key, and this service deliberately does not hold one —
///     IVoiceCatalogDirectory says so in as many words: "the provider API key stays confined to
///     the AI side". The AI side, in turn, holds no credentials for the bucket these samples live
///     in. Neither half can do the other's part, so the audio and the answer travel between them.
///
/// WHY REDIS
///     Because that is already how these two talk. tts_worker writes the voice catalogue into
///     Redis and RedisVoiceCatalogDirectory reads it — no HTTP surface serving biometric audio,
///     no service-to-service tokens to mint and expire, and no new dependency on either side.
///
/// WHY THE RESULT IS COLLECTED LAZILY
///     This service has never consumed anything. A background consumer would be a new lifecycle,
///     a new failure mode, and a new thing to watch, to deliver an answer nobody sees until they
///     open the page. Reading it while listing profiles puts it exactly where it is wanted.
/// </summary>
public interface IVoiceCloneRequestQueue
{
    /// <summary>
    /// Hand the sample over for cloning. Returns false when the request could not be queued —
    /// Redis is down, or the recording is larger than <see cref="MaxSampleBytes"/> — and the
    /// caller must treat that as "not cloned yet", never as a failed upload: the recording is
    /// already stored and the profile is still theirs.
    /// </summary>
    Task<bool> RequestAsync(
        Guid profileId,
        Guid userId,
        string language,
        byte[] sample,
        CancellationToken ct = default);

    /// <summary>
    /// Take the answer for this profile if one is waiting, removing it. Null means "still
    /// working, or never asked" — both of which read the same way to the caller and neither of
    /// which is an error.
    /// </summary>
    Task<VoiceCloneOutcome?> TakeOutcomeAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// The largest recording that will be sent for cloning.
    ///
    /// Cartesia builds a voice from roughly ten to thirty seconds; twenty seconds of mono
    /// 16-bit WAV is under a megabyte and a compressed upload far less. Eight is therefore
    /// generous by an order of magnitude, and it is a CAP rather than a truncation because
    /// cutting an audio file mid-container produces something no decoder will read.
    ///
    /// The bound exists because these bytes sit in Redis until they are collected, and Redis
    /// filling up is not hypothetical here: it reached 93% of 768 MB on 2026-08-14 and started
    /// silently evicting live meeting state.
    /// </summary>
    static int MaxSampleBytes => 8 * 1024 * 1024;
}

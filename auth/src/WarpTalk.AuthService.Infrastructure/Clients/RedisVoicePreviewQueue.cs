using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.AuthService.Application.Interfaces;

namespace WarpTalk.AuthService.Infrastructure.Clients;

/// <summary>
/// The Redis half of the preview hand-off. Raw StackExchange.Redis, like the clone queue beside
/// it and for the same reason: the other end is Python and IDistributedCache wraps values in an
/// envelope only .NET can read.
/// </summary>
public class RedisVoicePreviewQueue : IVoicePreviewQueue
{
    // Keyed by (voice, language), NOT by request. That is the whole caching strategy: a preview
    // of one voice is the same audio every time anybody asks, so the second play — and every
    // play after it — is a cache read rather than a paid synthesis.
    private const string ResultKeyPrefix = "voice:preview:";
    private const string RequestStream = "voice:preview_requests";

    // A handful of ids per entry and the audio is not in here. Deep enough to survive the AI
    // worker being down for a deploy, shallow enough that a burst of clicking cannot grow it.
    private const int RequestStreamMaxLength = 1_000;

    // How often the wait re-reads the key. Short enough that a fast render is not made to look
    // slow by the poll interval, long enough that a full timeout is a dozen round trips and not
    // hundreds.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisVoicePreviewQueue> _logger;

    public RedisVoicePreviewQueue(
        IConnectionMultiplexer redis,
        ILogger<RedisVoicePreviewQueue> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    /// <summary>
    /// The primary subtag, lower-cased: "vi-VN" -> "vi", "en_US" -> "en", "vi" -> "vi".
    ///
    /// WHY THIS EXISTS AND IS NOT COSMETIC
    ///     The other end of this key is Python, and `_handle_preview_request` writes its answer
    ///     under `base_language(language)` — it has always done so. Anything sent here with a
    ///     region therefore renders correctly, is cached correctly, and is then waited for under
    ///     a key nobody will ever write: "voice:preview:{id}:vi-VN" against an answer sitting at
    ///     "voice:preview:{id}:vi".
    ///
    ///     That is WT-632. The library-voice rows send a bare code and worked; the "Your voices"
    ///     rows send the profile's stored language, which is a locale tag ("vi-VN"), and timed
    ///     out on every press forever — while paying for a fresh Cartesia render each time,
    ///     because the cache read could not hit either. The unit tests all passed a bare "vi"
    ///     against a mocked queue, so nothing above this line could see it.
    ///
    ///     Normalized HERE rather than in the service because this class is the half of the
    ///     contract that names the key. Callers should not have to know its shape.
    /// </summary>
    private static string BaseLanguage(string language)
    {
        var trimmed = language?.Trim() ?? string.Empty;
        var separator = trimmed.IndexOfAny(new[] { '-', '_' });
        return (separator < 0 ? trimmed : trimmed[..separator]).ToLowerInvariant();
    }

    private static string KeyFor(string voiceId, string language) =>
        $"{ResultKeyPrefix}{voiceId}:{BaseLanguage(language)}";

    public async Task<VoicePreview?> TryGetAsync(
        string voiceId, string language, CancellationToken ct = default)
    {
        try
        {
            var raw = await _redis.GetDatabase().StringGetAsync(KeyFor(voiceId, language));
            if (raw.IsNullOrEmpty)
            {
                return null;
            }

            var envelope = JsonSerializer.Deserialize<PreviewEnvelope>(
                (string)raw!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (envelope is null)
            {
                return null;
            }

            // A rendered failure is a real answer and is returned as one — it is what lets the
            // page say the voice could not be rendered instead of spinning until the timeout.
            if (string.IsNullOrEmpty(envelope.Audio))
            {
                return new VoicePreview(null, envelope.Error ?? "The preview could not be rendered.");
            }

            return new VoicePreview(Convert.FromBase64String(envelope.Audio), null);
        }
        catch (Exception ex)
        {
            // Reading a preview must never break the page it is on. Absent reads as "not yet",
            // which the caller already handles.
            _logger.LogWarning(ex, "Could not read the voice preview for {VoiceId}.", voiceId);
            return null;
        }
    }

    public async Task<bool> RequestAsync(
        string voiceId, string language, CancellationToken ct = default)
    {
        try
        {
            await _redis.GetDatabase().StreamAddAsync(
                RequestStream,
                new NameValueEntry[]
                {
                    new("voice_id", voiceId),
                    // Sent in the same shape the answer is keyed by, so the request and the key
                    // it will be waited for under cannot disagree.
                    new("language", BaseLanguage(language)),
                },
                maxLength: RequestStreamMaxLength,
                useApproximateMaxLength: true);
            return true;
        }
        catch (Exception ex)
        {
            // Never fatal. The caller says the preview is unavailable, which is honest and
            // retryable, rather than waiting out a timeout for an answer nobody was asked for.
            _logger.LogWarning(ex, "Could not queue a voice preview for {VoiceId}.", voiceId);
            return false;
        }
    }

    public async Task<VoicePreview?> WaitAsync(
        string voiceId, string language, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + IVoicePreviewQueue.RenderTimeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                // The caller gave up (closed the tab, cancelled the request). A render already
                // in flight still lands in Redis and serves the next play instantly.
                return null;
            }

            var found = await TryGetAsync(voiceId, language, ct);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// The shape the AI side writes. Base64 rather than a second binary key, so a failure can be
    /// named in the same value instead of being indistinguishable from nothing written yet.
    /// </summary>
    private sealed record PreviewEnvelope(string? Audio, string? Error);
}

/// <summary>
/// Used where there is no Redis — the same local-run fallback NullVoiceCloneRequestQueue is.
/// Previews simply never render, and the page says the sample is unavailable.
/// </summary>
public class NullVoicePreviewQueue : IVoicePreviewQueue
{
    public Task<VoicePreview?> TryGetAsync(string voiceId, string language, CancellationToken ct = default)
        => Task.FromResult<VoicePreview?>(null);

    public Task<bool> RequestAsync(string voiceId, string language, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<VoicePreview?> WaitAsync(string voiceId, string language, CancellationToken ct = default)
        => Task.FromResult<VoicePreview?>(null);
}

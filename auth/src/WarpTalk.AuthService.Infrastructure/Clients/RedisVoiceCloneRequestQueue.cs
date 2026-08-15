using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.AuthService.Application.Interfaces;

namespace WarpTalk.AuthService.Infrastructure.Clients;

/// <summary>
/// The Redis half of the clone hand-off. Raw StackExchange.Redis, like
/// RedisVoiceCatalogDirectory beside it and for the same reason: the other end is Python and
/// IDistributedCache wraps values in an envelope only .NET can read.
/// </summary>
public class RedisVoiceCloneRequestQueue : IVoiceCloneRequestQueue
{
    // The audio itself, one key per profile. Suffixed with the profile id on purpose: the AI
    // side expires anything whose last path segment is a UUID (WT-402), so this inherits a
    // bounded lifetime from a rule that already exists instead of needing one of its own.
    private const string SampleKeyPrefix = "voice:clone_sample:";
    private const string ResultKeyPrefix = "voice:clone_result:";
    private const string RequestStream = "voice:clone_requests";

    // Long enough for a worker restart mid-deploy to still pick the job up, short enough that a
    // recording nobody ever clones does not sit in memory for a day.
    private static readonly TimeSpan SampleTtl = TimeSpan.FromHours(1);

    // The answer's own lifetime is set by the writer, on the AI side: it outlives the sample
    // because somebody may upload and not open the page again for days, and losing the voice id
    // would mean re-cloning — a second paid call for a result we already had.

    // Enough for the notification backlog to survive a worker being down for a deploy, and no
    // more: the payload is a handful of ids, and the audio is not in here.
    private const int RequestStreamMaxLength = 1_000;

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisVoiceCloneRequestQueue> _logger;

    public RedisVoiceCloneRequestQueue(
        IConnectionMultiplexer redis,
        ILogger<RedisVoiceCloneRequestQueue> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<bool> RequestAsync(
        Guid profileId,
        Guid userId,
        string language,
        byte[] sample,
        CancellationToken ct = default)
    {
        if (sample.Length == 0 || sample.Length > IVoiceCloneRequestQueue.MaxSampleBytes)
        {
            _logger.LogWarning(
                "Not queueing voice profile {ProfileId} for cloning: sample is {Bytes} bytes (limit {Limit}).",
                profileId, sample.Length, IVoiceCloneRequestQueue.MaxSampleBytes);
            return false;
        }

        try
        {
            var db = _redis.GetDatabase();

            // Audio first, notification second. The other order lets a fast worker read the
            // notification and find no sample, which it would report as a failed clone — a
            // permanent-looking error caused purely by ordering.
            await db.StringSetAsync(SampleKeyPrefix + profileId, sample, SampleTtl);

            await db.StreamAddAsync(
                RequestStream,
                new NameValueEntry[]
                {
                    new("profile_id", profileId.ToString()),
                    new("user_id", userId.ToString()),
                    new("language", language),
                },
                maxLength: RequestStreamMaxLength,
                useApproximateMaxLength: true);

            _logger.LogInformation(
                "Queued voice profile {ProfileId} for cloning ({Bytes} bytes).", profileId, sample.Length);
            return true;
        }
        catch (Exception ex)
        {
            // Never fatal to the upload. The recording is already stored and the profile row is
            // already theirs; what they lose is the clone, and the page says so rather than the
            // upload appearing to fail after it succeeded.
            _logger.LogWarning(ex, "Could not queue voice profile {ProfileId} for cloning.", profileId);
            return false;
        }
    }

    public async Task<VoiceCloneOutcome?> TakeOutcomeAsync(Guid profileId, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var key = ResultKeyPrefix + profileId;
            var raw = await db.StringGetAsync(key);
            if (raw.IsNullOrEmpty)
            {
                return null;
            }

            var outcome = JsonSerializer.Deserialize<VoiceCloneOutcome>(
                (string)raw!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (outcome is null)
            {
                // Unparseable is not "still working" — leaving it would re-read the same bad
                // value on every page load forever.
                await db.KeyDeleteAsync(key);
                _logger.LogWarning("Discarded an unreadable clone result for {ProfileId}.", profileId);
                return null;
            }

            await db.KeyDeleteAsync(key);
            return outcome;
        }
        catch (Exception ex)
        {
            // Reading an answer must never be able to break listing profiles.
            _logger.LogWarning(ex, "Could not read the clone result for {ProfileId}.", profileId);
            return null;
        }
    }
}

/// <summary>
/// Used where there is no Redis — the same local-run fallback EmptyVoiceCatalogDirectory exists
/// for. Cloning simply never starts, which is exactly the state the UI already renders for a
/// recording that has not been turned into a voice yet.
/// </summary>
public class NullVoiceCloneRequestQueue : IVoiceCloneRequestQueue
{
    public Task<bool> RequestAsync(
        Guid profileId, Guid userId, string language, byte[] sample, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<VoiceCloneOutcome?> TakeOutcomeAsync(Guid profileId, CancellationToken ct = default)
        => Task.FromResult<VoiceCloneOutcome?>(null);
}

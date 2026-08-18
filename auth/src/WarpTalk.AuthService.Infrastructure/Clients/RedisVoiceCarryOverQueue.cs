using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.AuthService.Application.Interfaces;

namespace WarpTalk.AuthService.Infrastructure.Clients;

/// <summary>
/// The Redis half of the carry-over hand-off. Raw StackExchange.Redis, like
/// RedisVoiceCloneRequestQueue beside it and for the same reason: the other end is Python.
/// </summary>
public class RedisVoiceCarryOverQueue : IVoiceCarryOverQueue
{
    private const string CarryOverStream = "voice:auto_clone_ready";
    private const string DeleteStream = "voice:delete_requests";
    private const string GroupName = "auth-carry-over";
    private const string ConsumerName = "auth-carry-over-consumer";

    // The payload is four small fields. This is a backlog for a deploy, not a queue.
    private const int DeleteStreamMaxLength = 1_000;

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisVoiceCarryOverQueue> _logger;
    private bool _groupReady;

    public RedisVoiceCarryOverQueue(
        IConnectionMultiplexer redis,
        ILogger<RedisVoiceCarryOverQueue> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    /// <summary>
    /// Create the consumer group if it is not there, tolerating the race and the empty stream.
    ///
    /// <c>createStream: true</c> matters: the AI side may not have cloned anybody yet, and
    /// XGROUP CREATE on a stream that does not exist fails rather than waiting.
    ///
    /// Re-runnable on purpose, and re-run after any read failure. A stream can be deleted out
    /// from under a running service — Redis here is allkeys-lru and has evicted live keys before
    /// — and a group that vanishes turns every later read into NOGROUP forever. That is exactly
    /// how the gateway went silently deaf while reporting healthy.
    /// </summary>
    private async Task EnsureGroupAsync()
    {
        if (_groupReady)
        {
            return;
        }

        try
        {
            await _redis.GetDatabase().StreamCreateConsumerGroupAsync(
                CarryOverStream, GroupName, StreamPosition.Beginning, createStream: true);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.Ordinal))
        {
            // Already there — the ordinary case on every start after the first.
        }

        _groupReady = true;
    }

    public async Task<IReadOnlyList<VoiceCarryOverMessage>> ReadAsync(
        int count, CancellationToken ct = default)
    {
        try
        {
            await EnsureGroupAsync();

            var entries = await _redis.GetDatabase().StreamReadGroupAsync(
                CarryOverStream, GroupName, ConsumerName, StreamPosition.NewMessages, count);

            var messages = new List<VoiceCarryOverMessage>(entries.Length);
            foreach (var entry in entries)
            {
                var parsed = Parse(entry);
                if (parsed is null)
                {
                    // Unreadable is not "try again later": redelivering it forever would wedge
                    // the group behind one bad entry. Drop it from our pending list and say so.
                    _logger.LogWarning(
                        "Discarding an unreadable voice carry-over entry {MessageId}.", entry.Id);
                    await AcknowledgeAsync(entry.Id!, ct);
                    continue;
                }

                messages.Add(parsed);
            }

            return messages;
        }
        catch (Exception ex)
        {
            // Force the group check to run again next time: the most likely cause of a read
            // failing is the stream or the group having gone away.
            _groupReady = false;
            _logger.LogWarning(ex, "Could not read the voice carry-over stream.");
            return Array.Empty<VoiceCarryOverMessage>();
        }
    }

    private VoiceCarryOverMessage? Parse(StreamEntry entry)
    {
        string? Field(string name)
        {
            foreach (var value in entry.Values)
            {
                if (value.Name == name)
                {
                    return value.Value.ToString();
                }
            }

            return null;
        }

        if (!Guid.TryParse(Field("user_id"), out var userId))
        {
            return null;
        }

        var voiceId = Field("voice_id");
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            return null;
        }

        var language = Field("language");
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        // InvariantCulture, and this is not a formality. The producer writes "0.812" with a dot
        // because Python does; a server running under a comma-decimal locale parses that with
        // TryParse's ambient culture as 812 — a score off by three orders of magnitude, silently,
        // in a value that decides whether a voice is ever replaced. Billing shipped exactly this
        // bug (0.006575 became 6575) and CI never saw it, because CI runs in en-US.
        var rawScore = Field("score");
        decimal? score = null;
        if (!string.IsNullOrWhiteSpace(rawScore)
            && decimal.TryParse(
                rawScore, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedScore))
        {
            score = parsedScore;
        }

        return new VoiceCarryOverMessage(entry.Id!, userId, language!.Trim(), voiceId!.Trim(), score);
    }

    public async Task AcknowledgeAsync(string messageId, CancellationToken ct = default)
    {
        try
        {
            await _redis.GetDatabase().StreamAcknowledgeAsync(CarryOverStream, GroupName, messageId);
        }
        catch (Exception ex)
        {
            // A lost ack redelivers, and the upsert is idempotent. Not worth failing anything.
            _logger.LogWarning(ex, "Could not acknowledge voice carry-over {MessageId}.", messageId);
        }
    }

    public async Task RequestDeletionAsync(
        string voiceId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            return;
        }

        try
        {
            await _redis.GetDatabase().StreamAddAsync(
                DeleteStream,
                new NameValueEntry[]
                {
                    new("voice_id", voiceId),
                    new("reason", reason),
                },
                maxLength: DeleteStreamMaxLength,
                useApproximateMaxLength: true);
        }
        catch (Exception ex)
        {
            // Loud, unlike the other failures here. For a consent withdrawal this is the promise
            // that the voice model itself goes away, and a voice that outlives the permission to
            // use it is the one failure on this path somebody will be asked about.
            _logger.LogError(
                ex,
                "Could not ask the AI side to delete voice {VoiceId} ({Reason}). The provider voice "
                + "may still exist.",
                voiceId, reason);
        }
    }
}

/// <summary>
/// Used where there is no Redis, like NullVoiceCloneRequestQueue beside it. Nothing is carried
/// over and nothing is destroyed, which is the behaviour that existed before any of this.
/// </summary>
public class NullVoiceCarryOverQueue : IVoiceCarryOverQueue
{
    public Task<IReadOnlyList<VoiceCarryOverMessage>> ReadAsync(
        int count, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<VoiceCarryOverMessage>>(Array.Empty<VoiceCarryOverMessage>());

    public Task AcknowledgeAsync(string messageId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RequestDeletionAsync(string voiceId, string reason, CancellationToken ct = default)
        => Task.CompletedTask;
}

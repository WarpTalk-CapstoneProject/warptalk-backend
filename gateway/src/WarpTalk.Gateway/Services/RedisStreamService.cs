using StackExchange.Redis;

namespace WarpTalk.Gateway.Services;

/// <summary>
/// Thin wrapper around StackExchange.Redis for Redis Streams.
/// Field serialization matches the Python AI worker schemas exactly.
/// </summary>
public sealed class RedisStreamService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisStreamService> _logger;
    private readonly int _streamMaxLength;

    public RedisStreamService(
        IConnectionMultiplexer redis,
        ILogger<RedisStreamService> logger,
        IConfiguration configuration)
    {
        _redis = redis;
        _logger = logger;
        _streamMaxLength = configuration.GetValue("Redis:StreamMaxLength", 10000);
    }

    // ── Publish ──────────────────────────────────────────────

    /// <summary>
    /// Publish an audio chunk to the AI pipeline.
    /// Fields match Python AudioChunkMessage.to_redis() exactly.
    /// </summary>
    public async Task<string> PublishAudioChunkAsync(
        string translationRoomId,
        string speakerId,
        int chunkIndex,
        string audioBase64,
        string language = "auto",
        int sampleRate = 16000,
        string sourceRuntime = "web",
        double vadConfidence = 0.0,
        int speechStartMs = 0,
        int speechEndMs = 0,
        double inputLufs = 0.0,
        bool noiseSuppressionEnabled = false)
    {
        var db = _redis.GetDatabase();
        var streamKey = $"audio:chunks:{translationRoomId}";
        var timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var entries = new NameValueEntry[]
        {
            new("translation_room_id", translationRoomId),
            new("speaker_id", speakerId),
            new("chunk_index", chunkIndex.ToString()),
            new("audio_data", audioBase64),
            new("language", language),
            new("sample_rate", sampleRate.ToString()),
            new("source_runtime", sourceRuntime),
            new("vad_confidence", vadConfidence.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("speech_start_ms", speechStartMs.ToString()),
            new("speech_end_ms", speechEndMs.ToString()),
            new("input_lufs", inputLufs.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("noise_suppression_enabled", noiseSuppressionEnabled ? "true" : "false"),
            new("timestamp_ms", timestampMs.ToString()),
        };

        var messageId = await db.StreamAddAsync(
            streamKey, entries, maxLength: _streamMaxLength, useApproximateMaxLength: true);

        _logger.LogDebug(
            "Published audio chunk to {StreamKey}: translationRoom={TranslationRoomId}, speaker={SpeakerId}, chunk={ChunkIndex}",
            streamKey, translationRoomId, speakerId, chunkIndex);

        return messageId.ToString();
    }

    /// <summary>
    /// Publish a room system event for TranslationRoomService to consume. WT-419.
    ///
    /// Field names match what TranslationRoomEventConsumerService reads — `event_type`, `room_id`,
    /// `route_id`, `payload` — and the stream is the one it already has a consumer group on. This
    /// is deliberately not a new channel: the existing one carries retry, a DLQ and a guarded
    /// consumer group, and a second path would have to grow all three again.
    /// </summary>
    public async Task<string> PublishSystemEventAsync(
        string translationRoomId,
        string eventType,
        string payloadJson,
        string routeId = "")
    {
        var db = _redis.GetDatabase();
        const string streamKey = "translationRoom:system_events";

        var entries = new NameValueEntry[]
        {
            new("event_type", eventType),
            new("room_id", translationRoomId),
            new("route_id", routeId),
            new("payload", payloadJson),
            new("timestamp_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()),
        };

        var messageId = await db.StreamAddAsync(
            streamKey, entries, maxLength: _streamMaxLength, useApproximateMaxLength: true);

        _logger.LogDebug(
            "Published {EventType} for room {RoomId} to {StreamKey}",
            eventType, translationRoomId, streamKey);

        return messageId.ToString();
    }

    // ── Consume ──────────────────────────────────────────────

    /// <summary>
    /// Ensures a consumer group exists on the stream. Creates it if missing.
    /// </summary>
    public async Task EnsureConsumerGroupAsync(string streamKey, string groupName)
    {
        var db = _redis.GetDatabase();
        try
        {
            await db.StreamCreateConsumerGroupAsync(streamKey, groupName, "0", createStream: true);
            _logger.LogInformation("Created consumer group {Group} on {Stream}", groupName, streamKey);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // Group already exists — this is fine
        }
    }

    /// <summary>
    /// Read new messages from a stream using consumer groups (XREADGROUP).
    /// Returns empty array if no messages available.
    /// </summary>
    public async Task<StreamEntry[]> ConsumeAsync(
        string streamKey,
        string groupName,
        string consumerName,
        int count = 10,
        int blockMs = 2000)
    {
        var db = _redis.GetDatabase();

        var result = await db.StreamReadGroupAsync(
            streamKey, groupName, consumerName,
            position: ">",
            count: count);

        return result;
    }

    /// <summary>
    /// Acknowledge a processed message.
    /// </summary>
    public async Task AcknowledgeAsync(string streamKey, string groupName, string messageId)
    {
        var db = _redis.GetDatabase();
        await db.StreamAcknowledgeAsync(streamKey, groupName, messageId);
    }

    /// <summary>
    /// Write a plain key with an expiry. Used to project decisions this gateway has
    /// already made (and cached) into Redis for the Python AI workers, which have no gRPC
    /// client and no service credentials of their own — Redis is the only channel they
    /// share with the .NET services.
    /// </summary>
    public async Task SetWithTtlAsync(string key, string value, TimeSpan ttl)
    {
        var db = _redis.GetDatabase();
        await db.StringSetAsync(key, value, ttl);
    }

    /// <summary>
    /// Read one field of a hash, or null when the hash or the field is absent.
    /// </summary>
    /// <remarks>
    /// Added for the live transcript's speaker names (WT-534). The AI ingress worker writes
    /// <c>meeting:{roomId}:speaker_names</c> as identity → display name, and this is the only way
    /// the gateway can read it: the name lives in auth, and putting a gRPC call on a per-segment
    /// realtime path would add a network hop to every sentence anybody says.
    /// </remarks>
    public async Task<string?> GetHashFieldAsync(string key, string field)
    {
        var db = _redis.GetDatabase();
        var value = await db.HashGetAsync(key, field);
        return value.IsNullOrEmpty ? null : value.ToString();
    }

    // ── Helpers ──────────────────────────────────────────────

    /// <summary>
    /// Extract a string field from a StreamEntry, handling both string and bytes keys.
    /// </summary>
    public static string? GetField(StreamEntry entry, string fieldName)
    {
        foreach (var nv in entry.Values)
        {
            if (nv.Name == fieldName)
                return nv.Value;
        }
        return null;
    }
}

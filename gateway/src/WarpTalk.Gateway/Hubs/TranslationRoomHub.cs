using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;
using System.Security.Claims;
using System.Collections.Concurrent;
using System.Text.Json;
using WarpTalk.Gateway.Services;

namespace WarpTalk.Gateway.Hubs;

/// <summary>
/// Real-time translationRoom communication hub.
/// Each translationRoom is a SignalR group: "translationRoom:{translationRoomId}".
/// All methods require JWT authentication.
/// </summary>
[Authorize]
public class TranslationRoomHub : Hub
{
    private readonly IConnectionManager _connectionManager;
    private readonly RedisStreamService _streamService;
    private readonly ActiveTranslationRoomRegistry _translationRoomRegistry;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<TranslationRoomHub> _logger;

    // Track which connection belongs to which room
    private static readonly ConcurrentDictionary<string, string> _connectionToRoom = new();
    
    // Track user active connection in a room: (RoomId_UserId) -> ConnectionId
    private static readonly ConcurrentDictionary<string, string> _roomUserToConnection = new();

    public TranslationRoomHub(
        IConnectionManager connectionManager,
        RedisStreamService streamService,
        ActiveTranslationRoomRegistry translationRoomRegistry,
        IConnectionMultiplexer redis,
        ILogger<TranslationRoomHub> logger)
    {
        _connectionManager = connectionManager;
        _streamService = streamService;
        _translationRoomRegistry = translationRoomRegistry;
        _redis = redis;
        _logger = logger;
    }

    // ── Lifecycle ─────────────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        var userId = GetUserId();
        _connectionManager.AddConnection(userId, Context.ConnectionId);

        _logger.LogInformation(
            "TranslationRoomHub: User {UserId} connected (ConnectionId: {ConnectionId})",
            userId, Context.ConnectionId);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = GetUserId();
        var isFullyOffline = _connectionManager.RemoveConnection(userId, Context.ConnectionId);

        // Room presence belongs to this TranslationRoomHub connection. The shared
        // connection manager also contains NotificationHub connections, so its
        // "fully offline" result cannot decide whether the participant left a room.
        // TryRemove still prevents an old connection (replaced by another device)
        // from publishing a false offline event.
        if (_connectionToRoom.TryRemove(Context.ConnectionId, out var roomIdStr))
        {
            var roomUserKey = $"{roomIdStr}_{userId}";
            _roomUserToConnection.TryRemove(roomUserKey, out _);

            // Publish event to Redis for TranslationRoomService to process participant left.
            // WT-08: MeetingService's HostFallbackConsumerWorker ALSO subscribes to this same
            // channel — if the departing user held the room's ActiveHostId, it elects a
            // replacement and broadcasts "HostChanged" back through the Gateway commands
            // channel (see MeetingRoomService.HandleHostOfflineAsync). Reused rather than
            // adding a second "translationRoom:host-offline" publish here, since the hub has
            // no cheap way to know host status itself (same trust-boundary gap as
            // SpotlightParticipant/MuteAll below) — the consumer re-derives that from the DB.
            var db = _redis.GetDatabase();
            await db.PublishAsync(RedisChannel.Literal("translationRoom:participant-offline"), $"{roomIdStr}:{userId}");

            _translationRoomRegistry.UnregisterParticipant(roomIdStr, userId);
            await db.HashDeleteAsync($"translationRoom:{roomIdStr}:languages", userId);
            await db.HashDeleteAsync($"translationRoom:{roomIdStr}:speak_languages", userId);
            await db.HashDeleteAsync($"translationRoom:{roomIdStr}:voice_preferences", userId);

            var groupName = TranslationRoomGroupName(Guid.Parse(roomIdStr));
            await Clients.OthersInGroup(groupName).SendAsync("ParticipantLeft", userId);
            // A hand left raised by a connection that never lowers it (crash, closed tab)
            // would otherwise stay stuck on everyone else's screen forever.
            await Clients.OthersInGroup(groupName).SendAsync("HandRaised", userId, false);
        }

        _logger.LogInformation(
            "TranslationRoomHub: User {UserId} disconnected (ConnectionId: {ConnectionId}, FullyOffline: {FullyOffline})",
            userId, Context.ConnectionId, isFullyOffline);

        await base.OnDisconnectedAsync(exception);
    }

    // ── Server Methods (Client → Server) ──────────────────

    /// <summary>
    /// Join a translationRoom room. Adds connection to the translationRoom group
    /// and broadcasts ParticipantJoined to other participants.
    /// </summary>
    public async Task JoinTranslationRoom(Guid translationRoomId, string displayName, string speakLanguage, string listenLanguage)
    {
        var userId = GetUserId();
        var groupName = TranslationRoomGroupName(translationRoomId);
        var roomIdStr = translationRoomId.ToString();
        var roomUserKey = $"{roomIdStr}_{userId}";
        var normalizedListenLanguage = NormalizeLanguageCode(listenLanguage);
        var normalizedSpeakLanguage = NormalizeLanguageCode(speakLanguage);

        // Enforce BR-159-014: Concurrent Session Limit (1 device per room)
        if (_roomUserToConnection.TryGetValue(roomUserKey, out var existingConnectionId))
        {
            if (existingConnectionId != Context.ConnectionId)
            {
                _logger.LogWarning("TranslationRoomHub: User {UserId} joined from a new device. Kicking old connection {OldConnectionId}.", userId, existingConnectionId);
                
                // Notify old connection
                await Clients.Client(existingConnectionId).SendAsync("ForceDisconnected", "You have joined from another device.");
                
                // Remove old connection from group
                await Groups.RemoveFromGroupAsync(existingConnectionId, groupName);
                _connectionToRoom.TryRemove(existingConnectionId, out _);
            }
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _connectionToRoom[Context.ConnectionId] = roomIdStr;
        _roomUserToConnection[roomUserKey] = Context.ConnectionId;

        var participantInfo = new ParticipantInfoDto(
            UserId: Guid.Parse(userId),
            DisplayName: displayName,
            SpeakLanguage: speakLanguage,
            ListenLanguage: normalizedListenLanguage,
            IsMuted: false,
            JoinedAt: DateTime.UtcNow);

        await Clients.OthersInGroup(groupName)
            .SendAsync("ParticipantJoined", participantInfo);

        // Register with AI pipeline — starts consuming AI results for this translationRoom
        _translationRoomRegistry.RegisterParticipant(translationRoomId.ToString(), userId);

        // Set target language for AI Translation Worker
        var db = _redis.GetDatabase();
        await db.HashSetAsync(
            $"translationRoom:{translationRoomId}:languages",
            userId,
            normalizedListenLanguage);

        // Every participant is simultaneously a translation source (when speaking) and a
        // target (their own listen language) — the AI pipeline previously had no visibility
        // into a speaker's own chosen language at all (only listen-language was persisted),
        // forcing STT to guess from text. Persist it (normalized to a bare "vi"/"en" — the
        // client may send locale-tagged values like "vi-VN") so livekit_ingress_worker can
        // pass a real per-speaker hint into STT instead of detecting from garbled output.
        await db.HashSetAsync(
            $"translationRoom:{translationRoomId}:speak_languages",
            userId,
            normalizedSpeakLanguage);

        _logger.LogInformation(
            "TranslationRoomHub: User {UserId} joined translationRoom {TranslationRoomId} (speak={SpeakLanguage}, listen={ListenLanguage})",
            userId, translationRoomId, normalizedSpeakLanguage, normalizedListenLanguage);
    }

    private static string NormalizeLanguageCode(string language) =>
        string.IsNullOrWhiteSpace(language) ? language : language.Split('-')[0].ToLowerInvariant();

    /// <summary>
    /// Leave a translationRoom room. Removes connection from the translationRoom group
    /// and broadcasts ParticipantLeft to remaining participants.
    /// </summary>
    public async Task LeaveTranslationRoom(Guid translationRoomId)
    {
        var userId = GetUserId();
        var groupName = TranslationRoomGroupName(translationRoomId);
        var roomIdStr = translationRoomId.ToString();
        var roomUserKey = $"{roomIdStr}_{userId}";

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        _connectionToRoom.TryRemove(Context.ConnectionId, out _);
        _roomUserToConnection.TryRemove(roomUserKey, out _);

        await Clients.OthersInGroup(groupName)
            .SendAsync("ParticipantLeft", userId);

        // A hand left raised when someone leaves would otherwise stay stuck for everyone else.
        await Clients.OthersInGroup(groupName)
            .SendAsync("HandRaised", userId, false);

        // Unregister from AI pipeline — stops consuming if last participant
        _translationRoomRegistry.UnregisterParticipant(translationRoomId.ToString(), userId);

        // Clean up language/voice preference
        var db = _redis.GetDatabase();
        await db.HashDeleteAsync($"translationRoom:{translationRoomId}:languages", userId);
        await db.HashDeleteAsync($"translationRoom:{translationRoomId}:speak_languages", userId);
        await db.HashDeleteAsync($"translationRoom:{translationRoomId}:voice_preferences", userId);

        _logger.LogInformation(
            "TranslationRoomHub: User {UserId} left translationRoom {TranslationRoomId}",
            userId, translationRoomId);
    }

    /// <summary>
    /// Toggle mute status and broadcast to all participants.
    /// </summary>
    public async Task ToggleMute(Guid translationRoomId, bool isMuted)
    {
        var userId = GetUserId();
        var groupName = TranslationRoomGroupName(translationRoomId);

        await Clients.OthersInGroup(groupName)
            .SendAsync("ParticipantMuteChanged", userId, isMuted);
    }

    /// <summary>
    /// Toggle the caller's raised-hand state and broadcast it to the rest of the room.
    /// Purely ephemeral (no persistence) — LeaveTranslationRoom and the fully-offline
    /// branch of OnDisconnectedAsync also emit HandRaised(userId, false) so a raised
    /// hand never stays stuck after someone leaves or disconnects.
    /// </summary>
    public async Task RaiseHand(Guid translationRoomId, bool isRaised)
    {
        var userId = GetUserId();
        var groupName = TranslationRoomGroupName(translationRoomId);

        await Clients.OthersInGroup(groupName)
            .SendAsync("HandRaised", userId, isRaised);
    }

    // Only these render as flying/fading reaction bubbles on the client — anything else
    // is rejected rather than silently broadcast.
    private static readonly HashSet<string> AllowedReactionEmojis = new() { "👍", "❤️", "😂", "🎉", "👏", "😮" };

    /// <summary>
    /// Broadcast an emoji reaction to EVERYONE in the room, including the caller, so the
    /// sender also sees their own reaction animate. Ephemeral — no persistence.
    /// </summary>
    public async Task SendReaction(Guid translationRoomId, string emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji) || !AllowedReactionEmojis.Contains(emoji))
            throw new HubException("Unsupported reaction emoji.");

        var userId = GetUserId();
        var groupName = TranslationRoomGroupName(translationRoomId);

        await Clients.Group(groupName)
            .SendAsync("ReactionReceived", userId, emoji, DateTime.UtcNow);
    }

    /// <summary>
    /// Broadcast collaborative meeting note changes to everyone in the room.
    /// </summary>
    public async Task SendCollaborativeNoteDelta(Guid translationRoomId, string noteContent)
    {
        var userId = GetUserId();
        var displayName = GetDisplayName();
        var groupName = TranslationRoomGroupName(translationRoomId);

        await Clients.OthersInGroup(groupName)
            .SendAsync("CollaborativeNoteUpdated", userId, displayName, noteContent, DateTime.UtcNow);
    }

    /// <summary>
    /// Host-only: admit a waiting participant from the queue into the live room.
    /// </summary>
    public async Task AdmitWaitingParticipant(Guid translationRoomId, string targetUserId)
    {
        var groupName = TranslationRoomGroupName(translationRoomId);

        await Clients.Group(groupName)
            .SendAsync("ParticipantAdmitted", targetUserId);

        _logger.LogInformation("TranslationRoomHub: Participant {TargetUserId} admitted to room {RoomId}", targetUserId, translationRoomId);
    }

    /// <summary>
    /// Host-only: force everyone's view to spotlight one participant.
    ///
    /// KNOWN GAP: unlike MeetingRoomService.TransferHostAsync (which can check
    /// room.ActiveHostId/HostId against the caller because it owns a DB/gRPC-backed
    /// room lookup), this Gateway hub has no injected repository or gRPC client for
    /// TranslationRoom/host data — only Redis, the connection registry, and the JWT
    /// claims already on Context.User. There is no cheap way to verify the caller is
    /// actually the room host from inside the hub today, so — like ToggleMute,
    /// SetListenLanguage, etc. — this trusts the caller's claimed identity from the JWT
    /// and does not verify host status server-side. A real fix needs either a gRPC
    /// client to TranslationRoomService injected into this hub, or a Redis-cached
    /// "translationRoom:{id}:hostId" value written by that service to check against.
    /// </summary>
    public async Task SpotlightParticipant(Guid translationRoomId, Guid targetUserId, bool on)
    {
        var groupName = TranslationRoomGroupName(translationRoomId);

        await Clients.Group(groupName)
            .SendAsync("SpotlightChanged", targetUserId, on);
    }

    /// <summary>
    /// Host-only (WT-04): force-mute every OTHER participant's mic. Each person can unmute
    /// themselves afterwards — this is not a hard/enforced mute, just a one-time nudge.
    ///
    /// KNOWN GAP: identical trust-boundary gap to SpotlightParticipant above, for the exact
    /// same reason — this Gateway hub has no injected repository/gRPC client for
    /// TranslationRoom/host data, only Redis, the connection registry, and JWT claims. There
    /// is no cheap way to verify the caller is actually the room host from inside the hub
    /// today, so this trusts the caller's claimed identity and does not verify host status
    /// server-side. A real fix needs the same thing SpotlightParticipant's comment describes:
    /// a gRPC client to TranslationRoomService injected into this hub, or a Redis-cached
    /// "translationRoom:{id}:hostId" value written by that service to check against.
    /// </summary>
    public async Task MuteAll(Guid translationRoomId)
    {
        var groupName = TranslationRoomGroupName(translationRoomId);

        await Clients.OthersInGroup(groupName)
            .SendAsync("ForceMuted");

        _logger.LogInformation("TranslationRoomHub: MuteAll invoked for translationRoom {TranslationRoomId}", translationRoomId);
    }

    /// <summary>
    /// Change the caller's own listen (output) language mid-meeting — lets a
    /// participant switch which dubbed track/transcript language they hear without
    /// leaving and rejoining the room. Previously listenLanguage was only ever set
    /// once, in JoinTranslationRoom; there was no way to change it without a full
    /// reconnect.
    ///
    /// Just updates the same `translationRoom:{id}:languages` hash translation_worker
    /// already reads per-utterance (see TranslationWorker._get_target_languages) — the
    /// AI pipeline picks up the new target language on the very next utterance, no
    /// pipeline restart needed. The client applies the switch to its own local state
    /// immediately (optimistic); this broadcast is only for OTHER clients that want to
    /// reflect a participant's current listen language (e.g. the people panel).
    /// </summary>
    public async Task SetListenLanguage(Guid translationRoomId, string listenLanguage)
    {
        if (string.IsNullOrWhiteSpace(listenLanguage))
            throw new HubException("listenLanguage is required.");

        var userId = GetUserId();
        var groupName = TranslationRoomGroupName(translationRoomId);
        var normalizedListenLanguage = NormalizeLanguageCode(listenLanguage);

        var db = _redis.GetDatabase();
        await db.HashSetAsync(
            $"translationRoom:{translationRoomId}:languages",
            userId,
            normalizedListenLanguage);

        await Clients.OthersInGroup(groupName)
            .SendAsync("ParticipantLanguageChanged", userId, normalizedListenLanguage);

        _logger.LogInformation(
            "TranslationRoomHub: User {UserId} changed listen language to {ListenLanguage} in translationRoom {TranslationRoomId}",
            userId, normalizedListenLanguage, translationRoomId);
    }

    /// <summary>
    /// Change the caller's own SPOKEN (source) language mid-meeting — the counterpart to
    /// SetListenLanguage above, but for what this participant is speaking rather than what
    /// they want to hear. livekit_ingress_worker reads this room's
    /// "translationRoom:{id}:speak_languages" hash fresh on every published speech chunk
    /// (no caching), so the change takes effect on the very next utterance for the STT
    /// allow-list and cross-script hallucination guard. The OpenAI Realtime session itself
    /// (stt_worker's per-(meeting,speaker) session, pinned to the language at creation time)
    /// re-pins on its own the next time a session is (re)created — see
    /// OpenAISTT._get_or_create_session's language-change eviction.
    /// </summary>
    public async Task SetSpeakLanguage(Guid translationRoomId, string speakLanguage)
    {
        if (string.IsNullOrWhiteSpace(speakLanguage))
            throw new HubException("speakLanguage is required.");

        var userId = GetUserId();
        var groupName = TranslationRoomGroupName(translationRoomId);
        var normalizedSpeakLanguage = NormalizeLanguageCode(speakLanguage);

        var db = _redis.GetDatabase();
        await db.HashSetAsync(
            $"translationRoom:{translationRoomId}:speak_languages",
            userId,
            normalizedSpeakLanguage);

        await Clients.OthersInGroup(groupName)
            .SendAsync("ParticipantSpeakLanguageChanged", userId, normalizedSpeakLanguage);

        _logger.LogInformation(
            "TranslationRoomHub: User {UserId} changed speak language to {SpeakLanguage} in translationRoom {TranslationRoomId}",
            userId, normalizedSpeakLanguage, translationRoomId);
    }

    /// <summary>
    /// Change the caller's own preferred TTS voice for the language they're currently
    /// listening in — mid-meeting, like SetListenLanguage. `voiceId` is a real Cartesia
    /// voice id (from GET /api/v1/translation-rooms/{id}/voices?language={lang}) or an
    /// empty string to clear the preference and fall back to the automatic per-speaker
    /// default (tts_worker._hashed_default_voice_id, or the speaker's cloned voice).
    ///
    /// Updates `translationRoom:{id}:voice_preferences` (userId -> voiceId), which
    /// tts_worker cross-references against `:languages` on every utterance (see
    /// TTSWorker._get_explicit_voice_choices) — the new voice applies from this
    /// listener's very next utterance heard, no pipeline restart needed. Unlike
    /// listen-language, a voice preference is scoped to whatever language the caller is
    /// CURRENTLY listening in; switching languages via SetListenLanguage does not carry
    /// a voice pick over (voices differ by Cartesia language table), so the client
    /// should treat a language change as clearing its own locally-remembered voice pick
    /// too.
    /// </summary>
    public async Task SetVoicePreference(Guid translationRoomId, string voiceId)
    {
        var userId = GetUserId();
        var groupName = TranslationRoomGroupName(translationRoomId);

        var db = _redis.GetDatabase();
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            await db.HashDeleteAsync($"translationRoom:{translationRoomId}:voice_preferences", userId);
        }
        else
        {
            await db.HashSetAsync(
                $"translationRoom:{translationRoomId}:voice_preferences",
                userId,
                voiceId);
        }

        await Clients.OthersInGroup(groupName)
            .SendAsync("ParticipantVoiceChanged", userId, voiceId);

        _logger.LogInformation(
            "TranslationRoomHub: User {UserId} changed voice preference to {VoiceId} in translationRoom {TranslationRoomId}",
            userId, voiceId, translationRoomId);
    }

    /// <summary>
    /// Real Cartesia voices tts_worker can render `language` in — for the control
    /// bar's voice picker. Reads the SAME Redis-cached catalog tts_worker itself uses
    /// (see TTSWorker._get_voice_catalog / CartesiaSynthesizer.list_voices), so every
    /// option returned here is guaranteed synthesizable — never a fabricated id.
    ///
    /// Returns an empty list (not an error) if tts_worker hasn't populated the cache
    /// for this language yet — that only happens once someone's speech has actually
    /// been translated into it since the cache last expired (voice_catalog_cache_ttl_
    /// seconds, 6h default). The client should treat an empty result as "no picker
    /// options available right now" and fall back to whatever the automatic default
    /// voice already provides, not surface it as a failure.
    /// </summary>
    // tts_worker writes voice_catalog:{language} as lowercase-key JSON (Python's
    // json.dumps of a plain dict — {"id":..., "name":..., "gender":...}).
    // JsonSerializer.Deserialize is case-SENSITIVE by default, which would silently
    // leave every VoiceCatalogEntry field null against those lowercase keys instead
    // of throwing — caught by GetVoiceCatalog_ShouldReturnParsedEntries_WhenCachePresent.
    private static readonly JsonSerializerOptions VoiceCatalogJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<List<VoiceOptionDto>> GetVoiceCatalog(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return new List<VoiceOptionDto>();

        var db = _redis.GetDatabase();
        var raw = await db.StringGetAsync($"voice_catalog:{NormalizeLanguageCode(language)}");
        if (raw.IsNullOrEmpty)
            return new List<VoiceOptionDto>();

        try
        {
            var entries = JsonSerializer.Deserialize<List<VoiceCatalogEntry>>((string)raw!, VoiceCatalogJsonOptions) ?? new();
            return entries
                .Select(e => new VoiceOptionDto(e.Id, e.Name, e.Gender ?? ""))
                .ToList();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "TranslationRoomHub: voice_catalog cache for {Language} was not valid JSON", language);
            return new List<VoiceOptionDto>();
        }
    }

    // Mirrors the dict shape tts_worker.CartesiaSynthesizer.list_voices() caches as
    // JSON — {"id": ..., "name": ..., "gender": ...} per entry.
    private sealed record VoiceCatalogEntry(string Id, string Name, string? Gender);

    /// <summary>
    /// Broadcast a live transcript segment to all translationRoom participants.
    /// Called by the AI pipeline (via internal service) or directly by clients.
    /// </summary>
    public async Task SendTranscriptSegment(Guid translationRoomId, TranscriptSegmentDto segment)
    {
        var groupName = TranslationRoomGroupName(translationRoomId);

        await Clients.Group(groupName)
            .SendAsync("TranscriptSegmentReceived", segment);
    }

    /// <summary>
    /// Send a chat message to all translationRoom participants.
    /// </summary>
    public async Task SendChatMessage(Guid translationRoomId, string content)
    {
        var userId = GetUserId();
        var displayName = GetDisplayName();

        var message = new ChatMessageDto(
            MessageId: Guid.NewGuid(),
            SenderId: Guid.Parse(userId),
            SenderName: displayName,
            Content: content,
            SentAt: DateTime.UtcNow);

        var groupName = TranslationRoomGroupName(translationRoomId);

        await Clients.Group(groupName)
            .SendAsync("ChatMessageReceived", message);
    }

    /// <summary>
    /// Broadcast that the translationRoom has ended to all participants.
    /// Typically called by the host or by the TranslationRoomService internally.
    /// </summary>
    public async Task EndTranslationRoom(Guid translationRoomId)
    {
        var groupName = TranslationRoomGroupName(translationRoomId);

        await Clients.Group(groupName)
            .SendAsync("TranslationRoomEnded", translationRoomId);

        _logger.LogInformation("TranslationRoomHub: TranslationRoom {TranslationRoomId} ended", translationRoomId);
    }

    /// <summary>
    /// Receive an audio chunk from the client and forward to the AI pipeline via Redis.
    /// Audio is base64-encoded on the client, forwarded as-is to the STT worker.
    /// </summary>
    public async Task SendAudioChunk(
        Guid translationRoomId,
        string audioBase64,
        int chunkIndex,
        string language = "auto",
        string sourceRuntime = "web",
        double vadConfidence = 0.0,
        int speechStartMs = 0,
        int speechEndMs = 0,
        double inputLufs = 0.0,
        bool noiseSuppressionEnabled = false)
    {
        var userId = GetUserId();

        await _streamService.PublishAudioChunkAsync(
            translationRoomId: translationRoomId.ToString(),
            speakerId: userId,
            chunkIndex: chunkIndex,
            audioBase64: audioBase64,
            language: language,
            sourceRuntime: sourceRuntime,
            vadConfidence: vadConfidence,
            speechStartMs: speechStartMs,
            speechEndMs: speechEndMs,
            inputLufs: inputLufs,
            noiseSuppressionEnabled: noiseSuppressionEnabled);

        _logger.LogDebug(
            "TranslationRoomHub: Audio chunk {ChunkIndex} from {UserId} in translationRoom {TranslationRoomId}",
            chunkIndex, userId, translationRoomId);
    }

    // ── Helpers ────────────────────────────────────────────

    private string GetUserId() =>
        Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? Context.User?.FindFirst("sub")?.Value
        ?? throw new HubException("User identity not found in token.");

    private string GetDisplayName() =>
        Context.User?.FindFirst(ClaimTypes.Name)?.Value
        ?? Context.User?.FindFirst("name")?.Value
        ?? "Unknown";

    private static string TranslationRoomGroupName(Guid translationRoomId) => $"translationRoom:{translationRoomId}";
}

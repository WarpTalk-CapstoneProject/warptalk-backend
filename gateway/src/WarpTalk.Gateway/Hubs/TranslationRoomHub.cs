using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;
using System.Security.Claims;
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

        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);

        var participantInfo = new ParticipantInfoDto(
            UserId: Guid.Parse(userId),
            DisplayName: displayName,
            SpeakLanguage: speakLanguage,
            ListenLanguage: listenLanguage,
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
            listenLanguage);

        _logger.LogInformation(
            "TranslationRoomHub: User {UserId} joined translationRoom {TranslationRoomId} (listen={ListenLanguage})",
            userId, translationRoomId, listenLanguage);
    }

    /// <summary>
    /// Leave a translationRoom room. Removes connection from the translationRoom group
    /// and broadcasts ParticipantLeft to remaining participants.
    /// </summary>
    public async Task LeaveTranslationRoom(Guid translationRoomId)
    {
        var userId = GetUserId();
        var groupName = TranslationRoomGroupName(translationRoomId);

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);

        await Clients.OthersInGroup(groupName)
            .SendAsync("ParticipantLeft", userId);

        // Unregister from AI pipeline — stops consuming if last participant
        _translationRoomRegistry.UnregisterParticipant(translationRoomId.ToString(), userId);

        // Clean up language preference
        var db = _redis.GetDatabase();
        await db.HashDeleteAsync($"translationRoom:{translationRoomId}:languages", userId);

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
    public async Task SendAudioChunk(Guid translationRoomId, string audioBase64, int chunkIndex, string language = "auto")
    {
        var userId = GetUserId();

        await _streamService.PublishAudioChunkAsync(
            translationRoomId: translationRoomId.ToString(),
            speakerId: userId,
            chunkIndex: chunkIndex,
            audioBase64: audioBase64,
            language: language);

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

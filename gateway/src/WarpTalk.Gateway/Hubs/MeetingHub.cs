using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;
using WarpTalk.Gateway.Services;

namespace WarpTalk.Gateway.Hubs;

/// <summary>
/// Real-time meeting communication hub.
/// Each meeting is a SignalR group: "meeting:{meetingId}".
/// All methods require JWT authentication.
/// </summary>
[Authorize]
public class MeetingHub : Hub
{
    private readonly IConnectionManager _connectionManager;
    private readonly RedisStreamService _streamService;
    private readonly ActiveMeetingRegistry _meetingRegistry;
    private readonly ILogger<MeetingHub> _logger;

    public MeetingHub(
        IConnectionManager connectionManager,
        RedisStreamService streamService,
        ActiveMeetingRegistry meetingRegistry,
        ILogger<MeetingHub> logger)
    {
        _connectionManager = connectionManager;
        _streamService = streamService;
        _meetingRegistry = meetingRegistry;
        _logger = logger;
    }

    // ── Lifecycle ─────────────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        var userId = GetUserId();
        _connectionManager.AddConnection(userId, Context.ConnectionId);

        _logger.LogInformation(
            "MeetingHub: User {UserId} connected (ConnectionId: {ConnectionId})",
            userId, Context.ConnectionId);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = GetUserId();
        var isFullyOffline = _connectionManager.RemoveConnection(userId, Context.ConnectionId);

        _logger.LogInformation(
            "MeetingHub: User {UserId} disconnected (ConnectionId: {ConnectionId}, FullyOffline: {FullyOffline})",
            userId, Context.ConnectionId, isFullyOffline);

        await base.OnDisconnectedAsync(exception);
    }

    // ── Server Methods (Client → Server) ──────────────────

    /// <summary>
    /// Join a meeting room. Adds connection to the meeting group
    /// and broadcasts ParticipantJoined to other participants.
    /// </summary>
    public async Task JoinMeeting(Guid meetingId, string displayName, string speakLanguage, string listenLanguage)
    {
        var userId = GetUserId();
        var groupName = MeetingGroupName(meetingId);

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

        // Register with AI pipeline — starts consuming AI results for this meeting
        _meetingRegistry.RegisterParticipant(meetingId.ToString(), userId);

        _logger.LogInformation(
            "MeetingHub: User {UserId} joined meeting {MeetingId}",
            userId, meetingId);
    }

    /// <summary>
    /// Leave a meeting room. Removes connection from the meeting group
    /// and broadcasts ParticipantLeft to remaining participants.
    /// </summary>
    public async Task LeaveMeeting(Guid meetingId)
    {
        var userId = GetUserId();
        var groupName = MeetingGroupName(meetingId);

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);

        await Clients.OthersInGroup(groupName)
            .SendAsync("ParticipantLeft", userId);

        // Unregister from AI pipeline — stops consuming if last participant
        _meetingRegistry.UnregisterParticipant(meetingId.ToString(), userId);

        _logger.LogInformation(
            "MeetingHub: User {UserId} left meeting {MeetingId}",
            userId, meetingId);
    }

    /// <summary>
    /// Toggle mute status and broadcast to all participants.
    /// </summary>
    public async Task ToggleMute(Guid meetingId, bool isMuted)
    {
        var userId = GetUserId();
        var groupName = MeetingGroupName(meetingId);

        await Clients.OthersInGroup(groupName)
            .SendAsync("ParticipantMuteChanged", userId, isMuted);
    }

    /// <summary>
    /// Broadcast a live transcript segment to all meeting participants.
    /// Called by the AI pipeline (via internal service) or directly by clients.
    /// </summary>
    public async Task SendTranscriptSegment(Guid meetingId, TranscriptSegmentDto segment)
    {
        var groupName = MeetingGroupName(meetingId);

        await Clients.Group(groupName)
            .SendAsync("TranscriptSegmentReceived", segment);
    }

    /// <summary>
    /// Send a chat message to all meeting participants.
    /// </summary>
    public async Task SendChatMessage(Guid meetingId, string content)
    {
        var userId = GetUserId();
        var displayName = GetDisplayName();

        var message = new ChatMessageDto(
            MessageId: Guid.NewGuid(),
            SenderId: Guid.Parse(userId),
            SenderName: displayName,
            Content: content,
            SentAt: DateTime.UtcNow);

        var groupName = MeetingGroupName(meetingId);

        await Clients.Group(groupName)
            .SendAsync("ChatMessageReceived", message);
    }

    /// <summary>
    /// Broadcast that the meeting has ended to all participants.
    /// Typically called by the host or by the MeetingService internally.
    /// </summary>
    public async Task EndMeeting(Guid meetingId)
    {
        var groupName = MeetingGroupName(meetingId);

        await Clients.Group(groupName)
            .SendAsync("MeetingEnded", meetingId);

        _logger.LogInformation("MeetingHub: Meeting {MeetingId} ended", meetingId);
    }

    /// <summary>
    /// Receive an audio chunk from the client and forward to the AI pipeline via Redis.
    /// Audio is base64-encoded on the client, forwarded as-is to the STT worker.
    /// </summary>
    public async Task SendAudioChunk(Guid meetingId, string audioBase64, int chunkIndex, string language = "auto")
    {
        var userId = GetUserId();

        await _streamService.PublishAudioChunkAsync(
            meetingId: meetingId.ToString(),
            speakerId: userId,
            chunkIndex: chunkIndex,
            audioBase64: audioBase64,
            language: language);

        _logger.LogDebug(
            "MeetingHub: Audio chunk {ChunkIndex} from {UserId} in meeting {MeetingId}",
            chunkIndex, userId, meetingId);
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

    private static string MeetingGroupName(Guid meetingId) => $"meeting:{meetingId}";
}

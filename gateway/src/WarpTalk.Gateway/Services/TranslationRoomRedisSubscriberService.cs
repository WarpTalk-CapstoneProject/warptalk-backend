using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;
using System.Text.Json;
using WarpTalk.Gateway.Hubs;

namespace WarpTalk.Gateway.Services;

/// <summary>
/// Background service acting as a Redis Pub/Sub subscriber.
/// Listens for new commands from TranslationRoomService (e.g. Kick, CancelRoom) 
/// and broadcasts them in real-time to the appropriate user's SignalR group.
/// </summary>
public class TranslationRoomRedisSubscriberService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IHubContext<TranslationRoomHub> _hubContext;
    private readonly ILogger<TranslationRoomRedisSubscriberService> _logger;

    public TranslationRoomRedisSubscriberService(
        IConnectionMultiplexer redis,
        IHubContext<TranslationRoomHub> hubContext,
        ILogger<TranslationRoomRedisSubscriberService> logger)
    {
        _redis = redis;
        _hubContext = hubContext;
        _logger = logger;
    }

    private const string CommandsChannel = "warptalk:translation-room:commands";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = _redis.GetSubscriber();
        await SubscribeWithRetryAsync(subscriber, stoppingToken, async (channel, message) =>
        {
            try
            {
                if (message.IsNullOrEmpty) return;

                var payload = JsonSerializer.Deserialize<TranslationRoomCommandMessage>(message.ToString());
                if (payload == null || string.IsNullOrEmpty(payload.Command)) return;

                if (payload.Command == "CancelRoom" && !string.IsNullOrEmpty(payload.RoomId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("ForceDisconnected", "This room has been cancelled.", stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted ForceDisconnected to room {RoomId}", payload.RoomId);
                }
                // WT-191: TranslationRoomService publishes this from EndTranslationRoomAsync.
                // The host ends the meeting over REST, so TranslationRoomHub.EndTranslationRoom
                // never runs and nothing else emits "TranslationRoomEnded" — the event the room
                // page has always listened for. Without this relay the remaining participants
                // stayed in an ended room until they pressed Leave.
                else if (payload.Command == "RoomEnded" && !string.IsNullOrEmpty(payload.RoomId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("TranslationRoomEnded", payload.RoomId, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted TranslationRoomEnded to room {RoomId}", payload.RoomId);
                }
                // WT-322: the mirror image of RoomEnded above. TranslationRoomService publishes
                // this from StartTranslationRoomAsync — the host starts the room over REST, so no
                // hub method runs and nothing else emitted "TranslationRoomStarted", the event the
                // meeting page has always listened for. Without this relay a participant already in
                // the room never learned translation went live, and the client flag that gates it
                // unsubscribes every interpreter track and drops every transcript segment, leaving
                // them on the untranslated raw microphones with no captions, indefinitely, while
                // the host saw translation running normally.
                // State is a pre-serialized (camelCase) TranslationRoomStateDto forwarded as-is,
                // the same arrangement PollCreated/QuestionAsked/BreakoutsStarted use below.
                else if (payload.Command == "RoomStarted" && !string.IsNullOrEmpty(payload.RoomId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("TranslationRoomStarted", payload.State, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted TranslationRoomStarted to room {RoomId}", payload.RoomId);
                }
                // The other half of the Start/Stop switch. TranslationRoomService publishes this
                // from StopTranslationAsync, which ends the room's translation session and leaves
                // the meeting — and its transcript — running.
                //
                // Start and Stop are room-wide, so this has to reach every participant and not
                // only the host who pressed it: each client re-reads the room's session list on
                // this event, which is what decides whether it prefers an interpreter dub over the
                // raw microphones. Without it the others kept preferring a dub nothing was
                // producing any more, until their own poll happened to notice.
                else if (payload.Command == "TranslationStopped" && !string.IsNullOrEmpty(payload.RoomId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("TranslationStopped", payload.RoomId, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted TranslationStopped to room {RoomId}", payload.RoomId);
                }
                // The waiting-room counterpart of RoomStarted/RoomEnded above.
                // TranslationRoomParticipantService publishes this from AdmitParticipantAsync —
                // the host approves over REST, so TranslationRoomHub.AdmitWaitingParticipant never
                // runs and nothing else emitted "ParticipantAdmitted". Without this relay the
                // admitted guest kept staring at "Waiting for Host": their participant poll is
                // disabled while they are in the lobby and their room query does not refetch on an
                // interval, so nothing on their side ever learned they had been let in.
                //
                // Broadcast to the whole room group, exactly like Kick below — every waiting client
                // is already in the group (JoinTranslationRoom runs regardless of waiting state),
                // and the one whose userId matches re-runs its join. Sending it to the group rather
                // than a single connection also means it survives the guest having reconnected on a
                // new connection id since they joined.
                else if (payload.Command == "ParticipantAdmitted"
                    && !string.IsNullOrEmpty(payload.RoomId)
                    && !string.IsNullOrEmpty(payload.UserId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("ParticipantAdmitted", payload.UserId, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted ParticipantAdmitted to room {RoomId} for user {UserId}", payload.RoomId, payload.UserId);
                }
                // WT-428: the knock — somebody just landed in the waiting room. Broadcast to the
                // whole room group like ParticipantAdmitted above; only hosts act on it (the web
                // client checks its own role), and the alternative — a userId→connection map for
                // the host alone — is state this relay deliberately does not keep.
                else if (payload.Command == "ParticipantWaiting"
                    && !string.IsNullOrEmpty(payload.RoomId)
                    && !string.IsNullOrEmpty(payload.UserId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync(
                        "ParticipantWaiting", payload.UserId, payload.DisplayName ?? string.Empty, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted ParticipantWaiting to room {RoomId} for user {UserId}", payload.RoomId, payload.UserId);
                }
                else if (payload.Command == "Kick" && !string.IsNullOrEmpty(payload.UserId))
                {
                    // Assuming ConnectionManager tracks users and we can broadcast to the user's specific connection.
                    // But we don't have user's connection ID here. Instead we can broadcast to all in the room,
                    // or broadcast to a global user group if we use UserId as group name.
                    // Here we broadcast to the room, and the client with matching UserId will disconnect.
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("ParticipantKicked", payload.UserId, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted ParticipantKicked to room {RoomId} for user {UserId}", payload.RoomId, payload.UserId);
                }
                // WT-04/WT-06/WT-08: MeetingService (a separate microservice/process from this
                // Gateway) publishes these on the same channel — it cannot inject
                // IHubContext<TranslationRoomHub> directly since it doesn't own this hub's
                // process; this Redis Pub/Sub relay is the established cross-process mechanism
                // (see MeetingRoomService.PublishGatewayCommandAsync).
                else if (payload.Command == "RoomLockChanged" && !string.IsNullOrEmpty(payload.RoomId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("RoomLockChanged", payload.Locked ?? false, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted RoomLockChanged({Locked}) to room {RoomId}", payload.Locked, payload.RoomId);
                }
                else if (payload.Command == "RecordingStateChanged" && !string.IsNullOrEmpty(payload.RoomId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("RecordingStateChanged", payload.Recording ?? false, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted RecordingStateChanged({Recording}) to room {RoomId}", payload.Recording, payload.RoomId);
                }
                else if (payload.Command == "HostChanged" && !string.IsNullOrEmpty(payload.RoomId) && !string.IsNullOrEmpty(payload.NewHostUserId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("HostChanged", payload.NewHostUserId, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted HostChanged({NewHostUserId}) to room {RoomId}", payload.NewHostUserId, payload.RoomId);
                }
                // Polls + Q&A: MeetingService.PollsService/QuestionsService publish these on the
                // same channel via the same REST+relay pattern as RoomLockChanged/HostChanged
                // above — Poll/Question/FinalResult/Tally are pre-serialized (camelCase) JSON
                // payloads forwarded to clients as-is.
                else if (payload.Command == "PollCreated" && !string.IsNullOrEmpty(payload.RoomId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("PollCreated", payload.Poll, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted PollCreated to room {RoomId}", payload.RoomId);
                }
                else if (payload.Command == "PollVoted" && !string.IsNullOrEmpty(payload.RoomId) && !string.IsNullOrEmpty(payload.PollId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("PollVoted", payload.PollId, payload.Tally, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted PollVoted({PollId}) to room {RoomId}", payload.PollId, payload.RoomId);
                }
                else if (payload.Command == "PollClosed" && !string.IsNullOrEmpty(payload.RoomId) && !string.IsNullOrEmpty(payload.PollId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("PollClosed", payload.PollId, payload.FinalResult, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted PollClosed({PollId}) to room {RoomId}", payload.PollId, payload.RoomId);
                }
                else if (payload.Command == "QuestionAsked" && !string.IsNullOrEmpty(payload.RoomId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("QuestionAsked", payload.Question, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted QuestionAsked to room {RoomId}", payload.RoomId);
                }
                else if (payload.Command == "QuestionUpvoted" && !string.IsNullOrEmpty(payload.RoomId) && !string.IsNullOrEmpty(payload.QuestionId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("QuestionUpvoted", payload.QuestionId, payload.UpvoteCount ?? 0, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted QuestionUpvoted({QuestionId}) to room {RoomId}", payload.QuestionId, payload.RoomId);
                }
                else if (payload.Command == "QuestionAnswered" && !string.IsNullOrEmpty(payload.RoomId) && !string.IsNullOrEmpty(payload.QuestionId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("QuestionAnswered", payload.QuestionId, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted QuestionAnswered({QuestionId}) to room {RoomId}", payload.QuestionId, payload.RoomId);
                }
                // Breakout rooms (scoped-down): MeetingService.BreakoutsService publishes these
                // on the same channel via the same REST+relay pattern as Polls/Q&A above.
                // Assignments carries NO LiveKit tokens (see BreakoutAssignmentRelayDto) — each
                // client that finds its own userId in the list fetches its own token via
                // GET .../breakouts/my-assignment.
                else if (payload.Command == "BreakoutsStarted" && !string.IsNullOrEmpty(payload.RoomId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("BreakoutsStarted", payload.Assignments, payload.DurationSeconds, payload.StartedAt, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted BreakoutsStarted to room {RoomId}", payload.RoomId);
                }
                else if (payload.Command == "BreakoutsEnded" && !string.IsNullOrEmpty(payload.RoomId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("BreakoutsEnded", stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted BreakoutsEnded to room {RoomId}", payload.RoomId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process incoming Redis translation-room command message.");
            }
        });

    }

    /// <summary>
    /// Subscribes with bounded backoff instead of letting the exception escape.
    ///
    /// An exception out of <see cref="ExecuteAsync"/> in a BackgroundService trips the default
    /// BackgroundServiceExceptionBehavior.StopHost, which for the Gateway means the whole
    /// application dies — YARP, every hub, every health endpoint — because Redis was a second
    /// late accepting connections during a parallel app/infra deploy. Same shape as
    /// HostFallbackConsumerWorker / ParticipantOfflineConsumerWorker / EntitlementsChangedConsumer.
    /// </summary>
    private async Task SubscribeWithRetryAsync(
        ISubscriber subscriber,
        CancellationToken stoppingToken,
        Action<RedisChannel, RedisValue> handler)
    {
        var retryDelay = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await subscriber.SubscribeAsync(RedisChannel.Literal(CommandsChannel), handler);
                _logger.LogInformation("TranslationRoomRedisSubscriberService started listening to '{Channel}'.", CommandsChannel);
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(
                    ex,
                    "TranslationRoomRedisSubscriberService could not subscribe to '{Channel}'; retrying in {RetryDelay}. "
                    + "Room commands (kick, lock, end, polls, breakouts) are not reaching clients until it succeeds.",
                    CommandsChannel,
                    retryDelay);
                await Task.Delay(retryDelay, stoppingToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }
    }
}

public class TranslationRoomCommandMessage
{
    public string Command { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;

    // WT-428: carried by ParticipantWaiting so the host's knock toast can say WHO is at the door
    // without a roster round-trip.
    public string? DisplayName { get; set; }

    // WT-04
    public bool? Locked { get; set; }

    // WT-06
    public bool? Recording { get; set; }

    // WT-08
    public string? NewHostUserId { get; set; }

    // WT-322 — the room's TranslationRoomStateDto, already serialized camelCase by
    // TranslationRoomService.PublishRoomStartedAsync and forwarded to clients untouched.
    public JsonElement? State { get; set; }

    // Polls + Q&A — Poll/Question/FinalResult are already-serialized (camelCase) JSON
    // element payloads produced by PollsService/QuestionsService; Tally is an
    // optionId(string) → count(int) map. Deserialized here as raw JsonElement and
    // forwarded to clients untouched (see ExecuteAsync above).
    public JsonElement? Poll { get; set; }
    public string? PollId { get; set; }
    public JsonElement? Tally { get; set; }
    public JsonElement? FinalResult { get; set; }
    public JsonElement? Question { get; set; }
    public string? QuestionId { get; set; }
    public int? UpvoteCount { get; set; }

    // Breakout rooms — Assignments is a pre-serialized (camelCase) JSON array of
    // {userId, sessionId, label}, forwarded to clients untouched (see ExecuteAsync above).
    public JsonElement? Assignments { get; set; }
    public int? DurationSeconds { get; set; }
    public string? StartedAt { get; set; }
}

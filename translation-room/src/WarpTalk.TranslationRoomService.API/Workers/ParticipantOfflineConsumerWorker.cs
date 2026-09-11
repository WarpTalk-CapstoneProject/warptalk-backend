using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Workers;

/// <summary>
/// Keeps the participant row in step with the hub socket: participant-offline marks a dropped
/// socket DISCONNECTED, participant-online restores it when the socket comes back. Both are
/// published by the Gateway's TranslationRoomHub.
/// </summary>
public class ParticipantOfflineConsumerWorker : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ParticipantOfflineConsumerWorker> _logger;

    public ParticipantOfflineConsumerWorker(
        IConnectionMultiplexer redis,
        IServiceProvider serviceProvider,
        ILogger<ParticipantOfflineConsumerWorker> logger)
    {
        _redis = redis;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    private const string ParticipantOfflineChannel = "translationRoom:participant-offline";
    private const string ParticipantOnlineChannel = "translationRoom:participant-online";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = _redis.GetSubscriber();

        // The app and infra roles deploy in parallel, so this worker can reach the subscribe
        // call before Redis is accepting connections. An exception escaping ExecuteAsync trips
        // the default BackgroundServiceExceptionBehavior.StopHost and takes the whole service
        // down, which turns a transient Redis blip into a failed deploy. Retry here instead.
        var retryDelay = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await subscriber.SubscribeAsync(
                    RedisChannel.Literal(ParticipantOfflineChannel),
                    async (channel, message) => await HandleParticipantOfflineAsync(message, stoppingToken));

                // WT-354 promised that a dropped socket "stays reversible when they reconnect";
                // this is the reversal. See MarkParticipantReconnectedAsync.
                await subscriber.SubscribeAsync(
                    RedisChannel.Literal(ParticipantOnlineChannel),
                    async (channel, message) => await HandleParticipantOnlineAsync(message, stoppingToken));

                _logger.LogInformation(
                    "ParticipantOfflineConsumerWorker started subscribing to '{OfflineChannel}' and '{OnlineChannel}'.",
                    ParticipantOfflineChannel,
                    ParticipantOnlineChannel);
                break;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "ParticipantOfflineConsumerWorker could not subscribe to '{Channel}'; retrying in {RetryDelay}.", ParticipantOfflineChannel, retryDelay);
                await Task.Delay(retryDelay, stoppingToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }

        // Wait indefinitely until cancellation is requested
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleParticipantOfflineAsync(RedisValue message, CancellationToken stoppingToken)
    {
        try
        {
            if (!TryParse(message, "participant-offline", out var roomId, out var userId)) return;

            _logger.LogInformation("Processing offline event for Room: {RoomId}, User: {UserId}", roomId, userId);

            using var scope = _serviceProvider.CreateScope();
            var participantService = scope.ServiceProvider.GetRequiredService<ITranslationRoomParticipantService>();

            // WT-354: this channel carries "the socket dropped", not "the participant left".
            // Calling LeaveRoomAsync here wrote the terminal status LEFT, which the roster hides,
            // so a backgrounded tab removed a live participant from everyone's People panel for
            // the rest of the meeting. MarkParticipantDisconnectedAsync records what actually
            // happened and stays reversible when they reconnect.
            var result = await participantService.MarkParticipantDisconnectedAsync(roomId, userId, stoppingToken);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Failed to mark {UserId} disconnected in {RoomId}: {Error}", userId, roomId, result.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing participant-offline message");
        }
    }

    private async Task HandleParticipantOnlineAsync(RedisValue message, CancellationToken stoppingToken)
    {
        try
        {
            if (!TryParse(message, "participant-online", out var roomId, out var userId)) return;

            using var scope = _serviceProvider.CreateScope();
            var participantService = scope.ServiceProvider.GetRequiredService<ITranslationRoomParticipantService>();

            var result = await participantService.MarkParticipantReconnectedAsync(roomId, userId, stoppingToken);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Failed to mark {UserId} reconnected in {RoomId}: {Error}", userId, roomId, result.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing participant-online message");
        }
    }

    private bool TryParse(RedisValue message, string channelName, out Guid roomId, out Guid userId)
    {
        roomId = Guid.Empty;
        userId = Guid.Empty;

        var payload = message.ToString();
        if (string.IsNullOrEmpty(payload)) return false;

        var parts = payload.Split(':');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out roomId) || !Guid.TryParse(parts[1], out userId))
        {
            _logger.LogWarning("Invalid {Channel} payload: {Payload}", channelName, payload);
            return false;
        }

        return true;
    }
}

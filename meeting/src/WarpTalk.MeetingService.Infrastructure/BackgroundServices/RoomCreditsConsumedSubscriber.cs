using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.MeetingService.Domain.Interfaces;

namespace WarpTalk.MeetingService.Infrastructure.BackgroundServices;

public class RoomCreditsConsumedSubscriber : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RoomCreditsConsumedSubscriber> _logger;
    private const string CreditsConsumedChannel = "warptalk:meeting:credits_consumed";
    private const string GatewayCommandsChannel = "warptalk:translation-room:commands";

    public RoomCreditsConsumedSubscriber(
        IConnectionMultiplexer redis,
        IServiceProvider serviceProvider,
        ILogger<RoomCreditsConsumedSubscriber> logger)
    {
        _redis = redis;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = _redis.GetSubscriber();

        await subscriber.SubscribeAsync(RedisChannel.Literal(CreditsConsumedChannel), async (channel, value) =>
        {
            if (value.IsNullOrEmpty) return;

            try
            {
                using var jsonDoc = JsonDocument.Parse(value.ToString());
                var root = jsonDoc.RootElement;
                if (!root.TryGetProperty("RoomId", out var roomIdProp) || !root.TryGetProperty("CreditsConsumed", out var creditsConsumedProp))
                {
                    return;
                }

                var roomId = roomIdProp.GetGuid();
                var creditsConsumed = creditsConsumedProp.GetInt32();

                using var scope = _serviceProvider.CreateScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                var meetingRoom = await unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == roomId);
                if (meetingRoom != null)
                {
                    meetingRoom.UsedToken += creditsConsumed;
                    unitOfWork.MeetingRoomRepository.Update(meetingRoom);
                    await unitOfWork.SaveChangesAsync();

                    // Publish to Gateway
                    var payload = new 
                    {
                        Command = "QuotaAdjusted",
                        RoomId = roomId.ToString(),
                        MaxQuota = meetingRoom.MaxQuota,
                        UsedToken = meetingRoom.UsedToken
                    };

                    await subscriber.PublishAsync(RedisChannel.Literal(GatewayCommandsChannel), JsonSerializer.Serialize(payload));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing credits consumed event");
            }
        });

        _logger.LogInformation("RoomCreditsConsumedSubscriber started.");

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}

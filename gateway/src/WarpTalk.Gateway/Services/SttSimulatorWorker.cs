using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace WarpTalk.Gateway.Services;

public class SttSimulatorWorker : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<SttSimulatorWorker> _logger;

    public SttSimulatorWorker(IConnectionMultiplexer redis, ILogger<SttSimulatorWorker> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("STT Simulator Worker starting...");
        while (!stoppingToken.IsCancellationRequested)
        {
            // For UAT Simulator: we could listen to 'room_start' events or just periodically generate fake transcripts 
            // for active rooms. For now, this is a placeholder.
            // A fully functional simulator would subscribe to Redis to know which rooms are ACTIVE.
            
            await Task.Delay(5000, stoppingToken);
        }
    }
}

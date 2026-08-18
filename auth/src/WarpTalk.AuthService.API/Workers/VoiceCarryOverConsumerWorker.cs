using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarpTalk.AuthService.Application.Interfaces;

namespace WarpTalk.AuthService.API.Workers;

/// <summary>
/// Keeps voices the AI side cloned during meetings (WT-B).
///
/// THE FIRST BACKGROUND SERVICE IN AUTHSERVICE, AND THAT IS A REAL COST
///     IVoiceCloneRequestQueue argues against exactly this, and it is right about its own case:
///     a background consumer is a new lifecycle, a new failure mode and a new thing to watch,
///     and the answer to an UPLOAD is wanted on the voice-profiles page, so it is collected when
///     that page opens.
///
///     A carried-over clone has no page. It is wanted by the route build at the start of that
///     person's next meeting, and nothing makes them visit any page in between. Pulled lazily it
///     would work only for people who happen to browse their voice settings.
///
/// EVERY LOOP IS INSIDE A TRY, DELIBERATELY
///     BackgroundServiceExceptionBehavior.StopHost is the default: an exception escaping
///     ExecuteAsync does not kill this worker, it kills AuthService — every login, every token
///     refresh, every gRPC answer this service gives. That is not theoretical here; a Redis blip
///     at startup took TranslationRoomService down exactly this way (WT-256), through a consumer
///     doing far less than this one. Nothing that happens on this path is worth an outage: the
///     failure mode of skipping a cycle is that somebody re-clones next meeting, which is what
///     everybody did before this existed.
/// </summary>
public class VoiceCarryOverConsumerWorker : BackgroundService
{
    /// <summary>Small: this is a trickle, one entry per accepted clone per speaker.</summary>
    private const int BatchSize = 20;

    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(30);

    private readonly IVoiceCarryOverQueue _queue;
    private readonly IServiceProvider _services;
    private readonly ILogger<VoiceCarryOverConsumerWorker> _logger;

    public VoiceCarryOverConsumerWorker(
        IVoiceCarryOverQueue queue,
        IServiceProvider services,
        ILogger<VoiceCarryOverConsumerWorker> logger)
    {
        _queue = queue;
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("VoiceCarryOverConsumerWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = IdleDelay;

            try
            {
                var messages = await _queue.ReadAsync(BatchSize, stoppingToken);
                if (messages.Count > 0)
                {
                    foreach (var message in messages)
                    {
                        if (stoppingToken.IsCancellationRequested)
                        {
                            break;
                        }

                        await ApplyOneAsync(message, stoppingToken);
                    }

                    // Straight back for the next batch while there is a backlog to clear.
                    delay = TimeSpan.Zero;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "VoiceCarryOverConsumerWorker cycle failed. Voices cloned in meetings are not "
                    + "being kept until it recovers; speakers will re-clone next meeting.");
                delay = ErrorDelay;
            }

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("VoiceCarryOverConsumerWorker stopped.");
    }

    /// <summary>
    /// One message, in its own scope and its own try.
    ///
    /// The scope is required: this is a singleton and the service it calls depends on a scoped
    /// unit of work. The inner try is what keeps ONE poisonous message from stalling the batch
    /// behind it — and the acknowledge only happens on success, so a message that fails for a
    /// transient reason is redelivered rather than dropped.
    /// </summary>
    private async Task ApplyOneAsync(VoiceCarryOverMessage message, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IVoiceCarryOverService>();

            await service.ApplyAsync(message, ct);
            await _queue.AcknowledgeAsync(message.MessageId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not keep the voice cloned for {UserId} ({Language}); it stays pending.",
                message.UserId, message.Language);
        }
    }
}

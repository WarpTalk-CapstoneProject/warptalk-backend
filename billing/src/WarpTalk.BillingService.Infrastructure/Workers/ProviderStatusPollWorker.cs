using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Options;
using WarpTalk.Shared.Coordination;

namespace WarpTalk.BillingService.Infrastructure.Workers;

/// <summary>
/// Polls each configured provider status page (ProviderStatus:Pages) every
/// <see cref="ProviderStatusOptions.PollIntervalMinutes"/>: the current indicator goes to
/// <see cref="IProviderStatusPageState"/>, the incident list into subscription.provider_status_incidents.
/// Optional — with no pages configured it logs once and stops. A failed poll keeps the last reading
/// and records why, and logs once per failure streak per provider. One replica per tick.
/// </summary>
public sealed class ProviderStatusPollWorker : BackgroundService
{
    public const string LockResource = "billing:provider-status-poll";
    public const string HttpClientName = "provider-status-pages";

    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpClientFactory _httpClients;
    private readonly IProviderStatusPageState _state;
    private readonly IDistributedLockProvider _locks;
    private readonly ProviderStatusOptions _options;
    private readonly ILogger<ProviderStatusPollWorker> _logger;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _failing = new(StringComparer.OrdinalIgnoreCase);

    public ProviderStatusPollWorker(
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClients,
        IProviderStatusPageState state,
        IDistributedLockProvider locks,
        IOptions<ProviderStatusOptions> options,
        ILogger<ProviderStatusPollWorker> logger,
        TimeProvider? time = null)
    {
        _serviceProvider = serviceProvider;
        _httpClients = httpClients;
        _state = state;
        _locks = locks;
        _options = options.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.PollIntervalMinutes <= 0 || _options.Pages.Count == 0)
        {
            _logger.LogInformation("ProviderStatusPollWorker is disabled (no ProviderStatus:Pages, or PollIntervalMinutes = 0).");
            return;
        }

        var interval = TimeSpan.FromMinutes(_options.PollIntervalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _locks.TryRunExclusiveAsync(LockResource, TimeSpan.FromMinutes(2), PollAllAsync, _logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ProviderStatusPollWorker: poll failed; retrying next tick.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task PollAllAsync(CancellationToken ct)
    {
        foreach (var (provider, url) in _options.Pages)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps) continue;
            await PollAsync(provider.ToLowerInvariant(), baseUri, ct);
        }
    }

    private async Task PollAsync(string provider, Uri baseUri, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var client = _httpClients.CreateClient(HttpClientName);
        try
        {
            var status = StatusPageParser.ParseStatus(await client.GetStringAsync(new Uri(baseUri, "/api/v2/status.json"), ct));
            var incidents = StatusPageParser.ParseIncidents(provider, await client.GetStringAsync(new Uri(baseUri, "/api/v2/incidents.json"), ct));

            using (var scope = _serviceProvider.CreateScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await unitOfWork.ProviderStatusIncidents.UpsertAsync(provider, incidents, now, ct);
            }

            await _state.SetAsync(new ProviderStatusPageSnapshot(provider, status.Indicator, status.Description, now, null), ct);
            if (_failing.Remove(provider)) _logger.LogInformation("Status page of {Provider} is readable again.", provider);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var previous = await _state.GetAsync(provider, ct);
            await _state.SetAsync(new ProviderStatusPageSnapshot(
                provider, previous?.Indicator, previous?.Description, previous?.CheckedAt, "last poll failed: " + ex.GetType().Name), ct);
            if (_failing.Add(provider)) _logger.LogWarning(ex, "Status page of {Provider} could not be read ({Url}).", provider, baseUri);
        }
    }
}

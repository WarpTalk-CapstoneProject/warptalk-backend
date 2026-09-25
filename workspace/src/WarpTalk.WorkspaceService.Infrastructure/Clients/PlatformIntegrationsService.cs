using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.Shared.PlatformSettings;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Clients;

/// <summary>
/// Assembles the Integrations section from the <c>platform:integrations:v1:{service}</c> hashes every
/// service (and every AI worker) writes about itself, and runs the connection tests this service can
/// run itself. A test is a harmless read — a PING, a readiness probe, a collection listing — and its
/// result (never a URL or credential) is kept for the "last check" column.
/// </summary>
public sealed class PlatformIntegrationsService : IPlatformIntegrationsService
{
    public const string HttpClientName = "platform-integrations";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>What this service can test from where it runs.</summary>
    public static readonly IReadOnlySet<string> Testable = new HashSet<string>(StringComparer.Ordinal)
    {
        IntegrationKeys.Redis, IntegrationKeys.Postgres, IntegrationKeys.Qdrant, IntegrationKeys.Prometheus, IntegrationKeys.Alertmanager,
    };

    private readonly IConnectionMultiplexer _redis;
    private readonly WorkspaceDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PlatformIntegrationsService> _logger;

    public PlatformIntegrationsService(
        IConnectionMultiplexer redis,
        WorkspaceDbContext db,
        IHttpClientFactory http,
        IConfiguration configuration,
        ILogger<PlatformIntegrationsService> logger)
    {
        _redis = redis;
        _db = db;
        _http = http;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<PlatformIntegrationsDto> GetAsync(CancellationToken ct = default)
    {
        var reports = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> checks = new Dictionary<string, string>();
        try
        {
            var db = _redis.GetDatabase();
            foreach (var key in await ReportKeysAsync(ct))
            {
                var entries = await db.HashGetAllAsync(key).WaitAsync(ct);
                if (entries.Length == 0) continue;
                reports[key[IntegrationStatusRedisKeys.Prefix.Length..]] =
                    entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString(), StringComparer.Ordinal);
            }

            checks = (await db.HashGetAllAsync(IntegrationStatusRedisKeys.ChecksHash).WaitAsync(ct))
                .ToDictionary(e => e.Name.ToString(), e => e.Value.ToString(), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // An unreadable Redis is itself the finding: every integration shows as "not reported".
            _logger.LogWarning(ex, "Integration reports could not be read.");
        }

        return Assemble(reports, checks);
    }

    /// <summary>
    /// Pure assembly of reports (service → field → JSON) and checks (key → JSON) into the console's
    /// view. Public so a test can hold its rules: unknown or malformed fields are skipped, and an
    /// integration is configured only when every service that reports it says so.
    /// </summary>
    public static PlatformIntegrationsDto Assemble(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> reports,
        IReadOnlyDictionary<string, string> checks)
    {
        var byIntegration = new Dictionary<string, List<IntegrationServiceReportDto>>(StringComparer.Ordinal);
        var deployConfig = new List<DeployConfigDto>();
        foreach (var (service, fields) in reports.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            foreach (var (field, json) in fields)
            {
                if (field == IntegrationStatusRedisKeys.DeployConfigField)
                {
                    foreach (var item in TryDeserialize<List<DeployConfigReport>>(json) ?? [])
                        deployConfig.Add(new DeployConfigDto(item.Key, item.Label, service, item.Value, item.Value is { Count: > 0 }));
                    continue;
                }

                if (IntegrationKeys.Find(field) is null || TryDeserialize<IntegrationReport>(json) is not { } report) continue;
                if (!byIntegration.TryGetValue(field, out var list)) byIntegration[field] = list = [];
                list.Add(new IntegrationServiceReportDto(service, report.Configured, Trim(report.Detail), report.ReportedAt));
            }
        }

        var integrations = IntegrationKeys.All.Select(definition =>
        {
            var services = byIntegration.TryGetValue(definition.Key, out var list) ? list : [];
            IntegrationCheckDto? lastCheck = checks.TryGetValue(definition.Key, out var check)
                ? TryDeserialize<IntegrationCheckDto>(check)
                : null;
            return new IntegrationStatusDto(
                definition.Key,
                definition.Name,
                services.Count == 0 ? null : services.All(s => s.Configured),
                services,
                lastCheck,
                Testable.Contains(definition.Key),
                definition.Href);
        }).ToList();

        return new PlatformIntegrationsDto(integrations, deployConfig);
    }

    public async Task<Result<IntegrationTestResultDto>> TestAsync(string key, CancellationToken ct = default)
    {
        if (IntegrationKeys.Find(key) is null)
            return Result.Failure<IntegrationTestResultDto>("Unknown integration.", ErrorCodes.NotFound);
        if (!Testable.Contains(key))
            return Result.Failure<IntegrationTestResultDto>(
                "This integration is not reachable from the workspace service; see its owning service's status instead.",
                ErrorCodes.ValidationError);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TestTimeout);
        var stopwatch = Stopwatch.StartNew();
        bool ok;
        string detail;
        try
        {
            (ok, detail) = key switch
            {
                IntegrationKeys.Redis => await PingRedisAsync(),
                IntegrationKeys.Postgres => (await _db.Database.CanConnectAsync(timeout.Token), "connected"),
                IntegrationKeys.Qdrant => await ProbeAsync("VectorDb:Url", "/collections", "VectorDb:ApiKey", timeout.Token),
                IntegrationKeys.Prometheus => await ProbeAsync("Monitoring:PrometheusUrl", "/-/ready", null, timeout.Token),
                IntegrationKeys.Alertmanager => await ProbeAsync("Monitoring:AlertmanagerUrl", "/-/ready", null, timeout.Token),
                _ => (false, "not testable"),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            (ok, detail) = (false, $"no answer within {TestTimeout.TotalSeconds:0} s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The exception type only: messages can carry hosts and connection strings.
            (ok, detail) = (false, ex.GetType().Name);
        }

        stopwatch.Stop();
        var result = new IntegrationTestResultDto(key, ok, stopwatch.ElapsedMilliseconds, detail, DateTimeOffset.UtcNow);
        try
        {
            await _redis.GetDatabase().HashSetAsync(
                IntegrationStatusRedisKeys.ChecksHash,
                key,
                JsonSerializer.Serialize(new IntegrationCheckDto(ok, result.CheckedAt, result.LatencyMs, detail), IntegrationStatusReporter.Json));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The result of the {Integration} check could not be stored.", key);
        }

        return Result.Success(result);
    }

    private async Task<(bool, string)> PingRedisAsync()
    {
        var latency = await _redis.GetDatabase().PingAsync();
        return (true, $"PONG in {latency.TotalMilliseconds:0} ms");
    }

    private async Task<(bool, string)> ProbeAsync(string urlKey, string path, string? apiKeyKey, CancellationToken ct)
    {
        var baseUrl = _configuration[urlKey];
        if (string.IsNullOrWhiteSpace(baseUrl)) return (false, "not configured");

        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + path);
        if (apiKeyKey is not null && _configuration[apiKeyKey] is { Length: > 0 } apiKey)
            request.Headers.TryAddWithoutValidation("api-key", apiKey);
        using var response = await _http.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return (response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
    }

    private async Task<IReadOnlyList<string>> ReportKeysAsync(CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in _redis.GetEndPoints())
        {
            var server = _redis.GetServer(endpoint);
            if (!server.IsConnected || server.IsReplica) continue;
            await foreach (var key in server.KeysAsync(pattern: IntegrationStatusRedisKeys.Prefix + "*", pageSize: 200).WithCancellation(ct))
            {
                var name = key.ToString();
                if (name != IntegrationStatusRedisKeys.ChecksHash) keys.Add(name);
            }
        }

        return keys.ToList();
    }

    private static T? TryDeserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, IntegrationStatusReporter.Json);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string? Trim(string? detail) => detail is { Length: > 200 } ? detail[..200] : detail;
}

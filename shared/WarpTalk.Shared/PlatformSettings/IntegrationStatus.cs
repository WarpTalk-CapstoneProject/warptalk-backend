using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace WarpTalk.Shared.PlatformSettings;

/// <summary>
/// The third-party integrations the settings console reports on. Each service knows only its own
/// configuration, so each one reports what it sees (<see cref="IntegrationStatusReporter"/>) and the
/// workspace service assembles the picture. The AI workers report the same way
/// (warptalk-ai <c>shared/integration_status.py</c>, hashes <c>platform:integrations:v1:ai-*</c>).
///
/// NEVER A SECRET: a report says whether something is configured, and at most a non-secret detail
/// (a model name, a region, the public list of allowed origins). No key, token, password or
/// connection string is ever written — not even a prefix of one.
/// </summary>
public static class IntegrationKeys
{
    public const string Stripe = "stripe";
    public const string LiveKit = "livekit";
    public const string OpenAi = "openai";
    public const string Cartesia = "cartesia";
    public const string Resend = "resend";
    public const string GoogleOAuth = "google_oauth";
    public const string ObjectStorage = "object_storage";
    public const string Qdrant = "qdrant";
    public const string Prometheus = "prometheus";
    public const string Alertmanager = "alertmanager";
    public const string Grafana = "grafana";
    public const string Redis = "redis";
    public const string Postgres = "postgres";

    public sealed record Definition(string Key, string Name, string? Href);

    /// <summary>In the order the console lists them. Href: where the live call statistics are.</summary>
    public static readonly IReadOnlyList<Definition> All =
    [
        new(Stripe, "Stripe", "/admin/providers"),
        new(LiveKit, "LiveKit", "/admin/providers"),
        new(OpenAi, "OpenAI", "/admin/providers"),
        new(Cartesia, "Cartesia", "/admin/providers"),
        new(Resend, "Resend (e-mail)", null),
        new(GoogleOAuth, "Google sign-in", null),
        new(ObjectStorage, "Object storage (R2 / MinIO)", null),
        new(Qdrant, "Vector database (Qdrant)", null),
        new(Prometheus, "Prometheus", "/admin/health"),
        new(Alertmanager, "Alertmanager", "/admin/health"),
        new(Grafana, "Grafana", "/admin/health"),
        new(Redis, "Redis", null),
        new(Postgres, "PostgreSQL", null),
    ];

    public static Definition? Find(string? key) => All.FirstOrDefault(d => d.Key == key);
}

/// <summary>One service's view of one integration.</summary>
public sealed record IntegrationReport(
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("reportedAt")] DateTimeOffset? ReportedAt = null)
{
    /// <summary>Configured when every named configuration value is non-empty. The values go nowhere.</summary>
    public static IntegrationReport FromConfiguration(IConfiguration configuration, string? detail, params string[] keys)
        => new(keys.Length > 0 && keys.All(k => !string.IsNullOrWhiteSpace(configuration[k])
                                                && !configuration[k]!.StartsWith("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
                                                && !configuration[k]!.Contains("placeholder", StringComparison.OrdinalIgnoreCase)),
               detail);

    /// <summary>
    /// The object store behind <c>Storage:*</c>: configured when the provider is S3-compatible (R2,
    /// MinIO) and its credentials are set. Local disk is reported as not configured, with the
    /// provider as the detail — it works on one machine and loses files on the next deploy.
    /// </summary>
    public static IntegrationReport ObjectStorage(IConfiguration configuration, string purpose)
    {
        var provider = configuration["Storage:Provider"];
        var s3 = new WarpTalk.Shared.Configuration.ObjectStorageOptions { Provider = provider ?? "Local" }.UsesS3CompatibleProvider;
        var configured = s3 && FromConfiguration(configuration, null, "Storage:S3:AccessKey", "Storage:S3:SecretKey", "Storage:S3:BucketName").Configured;
        return new IntegrationReport(configured, $"{purpose} · {(string.IsNullOrWhiteSpace(provider) ? "Local" : provider)}");
    }
}

/// <summary>A non-secret piece of deploy-time configuration shown read-only in the console.</summary>
public sealed record DeployConfigReport(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("value")] IReadOnlyList<string>? Value);

public static class IntegrationStatusRedisKeys
{
    public const string Prefix = "platform:integrations:v1:";
    public const string ChecksHash = Prefix + "checks";

    /// <summary>The hash field that carries a service's <see cref="DeployConfigReport"/> list.</summary>
    public const string DeployConfigField = "__config";

    public static string ServiceHash(string service) => Prefix + service;

    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
}

/// <summary>What one service reports: its integrations and any read-only deploy configuration.</summary>
public sealed record IntegrationStatusSnapshot(
    IReadOnlyDictionary<string, IntegrationReport> Integrations,
    IReadOnlyList<DeployConfigReport> DeployConfig);

/// <summary>
/// Writes this service's <see cref="IntegrationStatusSnapshot"/> to
/// <c>platform:integrations:v1:{service}</c> at start-up and every five minutes, with a ten-minute
/// expiry (so a service that stops reporting disappears instead of looking healthy forever).
/// Every tick is guarded: a failed report is logged and never stops the host.
/// </summary>
public sealed class IntegrationStatusReporter : BackgroundService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _service;
    private readonly Func<IServiceProvider, IntegrationStatusSnapshot> _probe;
    private readonly IServiceProvider _services;
    private readonly ILogger<IntegrationStatusReporter> _logger;

    public IntegrationStatusReporter(
        string service,
        Func<IServiceProvider, IntegrationStatusSnapshot> probe,
        IServiceProvider services,
        ILogger<IntegrationStatusReporter> logger)
    {
        _service = service;
        _probe = probe;
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            await ReportAsync(stoppingToken);
            try
            {
                await Task.Delay(IntegrationStatusRedisKeys.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task<bool> ReportAsync(CancellationToken ct)
    {
        try
        {
            var redis = _services.GetService<IConnectionMultiplexer>();
            if (redis is null) return false;
            var entries = Entries(_probe(_services), DateTimeOffset.UtcNow);
            var db = redis.GetDatabase();
            var key = IntegrationStatusRedisKeys.ServiceHash(_service);
            await db.HashSetAsync(key, entries).WaitAsync(ct);
            await db.KeyExpireAsync(key, IntegrationStatusRedisKeys.Ttl).WaitAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Integration status for {Service} could not be reported.", _service);
            return false;
        }
    }

    /// <summary>The hash fields written for a snapshot. Public so a test can hold it free of secrets.</summary>
    public static HashEntry[] Entries(IntegrationStatusSnapshot snapshot, DateTimeOffset now)
    {
        var entries = snapshot.Integrations
            .Select(pair => new HashEntry(pair.Key, JsonSerializer.Serialize(pair.Value with { ReportedAt = now }, Json)))
            .ToList();
        if (snapshot.DeployConfig.Count > 0)
            entries.Add(new HashEntry(IntegrationStatusRedisKeys.DeployConfigField, JsonSerializer.Serialize(snapshot.DeployConfig, Json)));
        return entries.ToArray();
    }
}

public static class IntegrationStatusServiceCollectionExtensions
{
    /// <summary>
    /// Reports this service's integrations to the settings console. <paramref name="probe"/> reads
    /// configuration and returns booleans and non-secret details only.
    /// </summary>
    public static IServiceCollection AddWarpTalkIntegrationStatus(
        this IServiceCollection services,
        string service,
        Func<IServiceProvider, IntegrationStatusSnapshot> probe)
    {
        services.AddHostedService(sp => new IntegrationStatusReporter(
            service, probe, sp, sp.GetRequiredService<ILogger<IntegrationStatusReporter>>()));
        return services;
    }

    /// <summary>Shorthand for a probe that only reports integrations.</summary>
    public static IntegrationStatusSnapshot Snapshot(params (string Key, IntegrationReport Report)[] reports)
        => new(reports.ToDictionary(r => r.Key, r => r.Report, StringComparer.Ordinal), []);
}

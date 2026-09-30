using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Services;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;

namespace WarpTalk.WorkspaceService.Infrastructure.Clients;

/// <summary>Where the inbox finds each owning service over HTTP (G12). Section <c>AdminInbox</c>.</summary>
public sealed class AdminInboxOptions
{
    public const string SectionName = "AdminInbox";

    public string BillingUrl { get; set; } = "http://localhost:5107";
    public string AuthUrl { get; set; } = "http://localhost:5101";
    public string NotificationUrl { get; set; } = "http://localhost:5104";

    /// <summary>Per source. A slow source is reported unavailable rather than holding the whole inbox.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    public static AdminInboxOptions From(IConfiguration configuration)
        => configuration.GetSection(SectionName).Get<AdminInboxOptions>() ?? new AdminInboxOptions();
}

/// <summary>
/// Reads each inbox source (G12). Remote ones are the owning service's <c>…/inbox-items</c> endpoint,
/// called directly (not through the gateway) with the staff member's own bearer token, so each service
/// applies its own permission to its own data. Operations — this service's outbox dead letters — is read
/// from the local database. Every failure becomes a status, never an exception.
/// </summary>
public sealed class AdminInboxSourceClient : IAdminInboxSourceClient
{
    public const string HttpClientName = "admin-inbox";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyList<AdminInboxSourceDefinition> Definitions =
    [
        new(AdminInbox.Sources.Billing, AdminPermissions.BillingRead),
        new(AdminInbox.Sources.Providers, AdminPermissions.ProvidersRead),
        new(AdminInbox.Sources.Expenses, AdminPermissions.FinanceRead),
        new(AdminInbox.Sources.Staff, AdminPermissions.StaffRead),
        new(AdminInbox.Sources.Content, AdminPermissions.ContentAnnouncements),
        new(AdminInbox.Sources.Operations, AdminPermissions.HealthRead),
    ];

    private readonly IHttpClientFactory _http;
    private readonly IHttpContextAccessor _context;
    private readonly WorkspaceDbContext _db;
    private readonly AdminInboxOptions _options;
    private readonly ILogger<AdminInboxSourceClient> _logger;
    private readonly TimeProvider _time;

    public AdminInboxSourceClient(
        IHttpClientFactory http,
        IHttpContextAccessor context,
        WorkspaceDbContext db,
        AdminInboxOptions options,
        ILogger<AdminInboxSourceClient> logger,
        TimeProvider? time = null)
    {
        _http = http;
        _context = context;
        _db = db;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<AdminInboxSourceDefinition> Sources => Definitions;

    public async Task<AdminInboxSourceResult> ReadAsync(string source, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var response = source == AdminInbox.Sources.Operations
                ? await ReadDeadLettersAsync(ct)
                : await ReadRemoteAsync(source, ct);
            return new AdminInboxSourceResult(source, response, AdminInboxService.SourceStatuses.Ok, watch.ElapsedMilliseconds, null);
        }
        catch (SourceForbiddenException)
        {
            return new AdminInboxSourceResult(source, null, AdminInboxService.SourceStatuses.Forbidden, watch.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Inbox source {Source} timed out after {Seconds}s.", source, _options.TimeoutSeconds);
            return Unavailable(source, watch, $"No answer within {_options.TimeoutSeconds} s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Inbox source {Source} failed.", source);
            return Unavailable(source, watch, ex is HttpRequestException http && http.StatusCode is { } code
                ? $"The service answered {(int)code}."
                : "The service could not be reached.");
        }
    }

    private static AdminInboxSourceResult Unavailable(string source, Stopwatch watch, string error)
        => new(source, null, AdminInboxService.SourceStatuses.Unavailable, watch.ElapsedMilliseconds, error);

    private sealed class SourceForbiddenException : Exception
    {
    }

    private async Task<AdminInboxSourceResponse?> ReadRemoteAsync(string source, CancellationToken ct)
    {
        var url = source switch
        {
            AdminInbox.Sources.Billing => Combine(_options.BillingUrl, "api/v1/admin/billing/inbox-items"),
            AdminInbox.Sources.Providers => Combine(_options.BillingUrl, "api/v1/admin/providers/inbox-items"),
            AdminInbox.Sources.Expenses => Combine(_options.BillingUrl, "api/v1/admin/billing/expenses/inbox-items"),
            AdminInbox.Sources.Staff => Combine(_options.AuthUrl, "api/v1/admin/staff/inbox-items"),
            AdminInbox.Sources.Content => Combine(_options.NotificationUrl, "api/v1/admin/notifications/inbox-items"),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown inbox source."),
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var authorization = _context.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(authorization)) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        var correlation = _context.HttpContext?.Request.Headers["X-Correlation-ID"].ToString();
        if (!string.IsNullOrWhiteSpace(correlation)) request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlation);

        using var response = await _http.CreateClient(HttpClientName).SendAsync(request, timeout.Token);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) throw new SourceForbiddenException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AdminInboxSourceResponse>(Json, timeout.Token);
    }

    private static string Combine(string baseUrl, string path) => baseUrl.TrimEnd('/') + "/" + path;

    /// <summary>
    /// Dead-lettered outbox events of this service: each is an event some consumer never got. Leaves the
    /// inbox when replayed from System health (replay clears dead_lettered_at).
    /// </summary>
    private async Task<AdminInboxSourceResponse> ReadDeadLettersAsync(CancellationToken ct)
    {
        var rows = await _db.WorkspaceOutboxMessages.AsNoTracking()
            .Where(m => m.DeadLetteredAt != null)
            .OrderByDescending(m => m.DeadLetteredAt)
            .Take(AdminInbox.MaxItemsPerSource + 1)
            .Select(m => new { m.Id, m.EventType, m.WorkspaceId, m.DeadLetteredAt, m.LastError, m.AttemptCount })
            .ToListAsync(ct);

        var items = rows.Take(AdminInbox.MaxItemsPerSource).Select(m =>
        {
            var at = DateTime.SpecifyKind(m.DeadLetteredAt!.Value, DateTimeKind.Utc);
            var error = m.LastError is { Length: > 160 } e ? e[..160] + "…" : m.LastError;
            return new AdminInboxItem(
                $"{AdminInbox.Types.DeadLetter}:workspace:{m.Id}",
                AdminInbox.Types.DeadLetter,
                $"Event {m.EventType} dead-lettered after {m.AttemptCount} attempts",
                error,
                m.WorkspaceId,
                null,
                at,
                at.AddHours(4),
                AdminInbox.Priorities.High,
                "/admin/health",
                NaturalCompletion: true);
        }).ToList();

        return new AdminInboxSourceResponse(AdminInbox.Sources.Operations, _time.GetUtcNow().UtcDateTime, items, rows.Count > AdminInbox.MaxItemsPerSource);
    }
}

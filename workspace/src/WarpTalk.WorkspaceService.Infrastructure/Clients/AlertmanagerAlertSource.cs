using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.Infrastructure.Clients;

/// <summary>
/// Reads the alerts a person is actually being told about, from Alertmanager's v2 API.
///
/// WHY NOT PROMETHEUS
///     <c>/api/v1/alerts</c> on Prometheus is rule state. It lists an alert whether or not anyone
///     silenced it and whether or not a more important alert inhibits it. On 24 Sep the kubeadm
///     control-plane alerts (etcd, scheduler, controller-manager, kube-proxy) were silenced in
///     Alertmanager for 30 days, and every one of them still sat on the System Health screen as
///     firing. Silences and inhibitions exist only in Alertmanager, so that is where this asks.
///
/// WHAT IS LEFT OUT
///     <c>Watchdog</c> fires permanently by design (it proves the pipeline delivers) and
///     <c>InfoInhibitor</c> exists only to inhibit info-level alerts. Neither is a problem, and on
///     a screen headed "firing alerts" both would read as one.
///
/// GET only. Nothing here can create or expire a silence.
/// </summary>
public sealed class AlertmanagerAlertSource : IPlatformAlertSource
{
    /// <summary>Firing, and neither silenced nor inhibited. <c>unprocessed</c> alerts have not been routed yet.</summary>
    public const string AlertsPath =
        "api/v2/alerts?active=true&silenced=false&inhibited=false&unprocessed=false";

    private static readonly HashSet<string> HeartbeatAlerts = new(StringComparer.Ordinal)
    {
        "Watchdog",
        "InfoInhibitor",
    };

    private readonly HttpClient _http;

    public AlertmanagerAlertSource(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<PlatformAlert>> FiringAlertsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(AlertsPath, ct);
        response.EnsureSuccessStatusCode();

        using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(ct)
            ?? throw new InvalidOperationException("Alertmanager returned an empty body.");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Alertmanager did not return a list of alerts.");
        }

        var results = new List<PlatformAlert>(document.RootElement.GetArrayLength());
        foreach (var alert in document.RootElement.EnumerateArray())
        {
            var labels = alert.TryGetProperty("labels", out var l) ? l : default;
            var annotations = alert.TryGetProperty("annotations", out var a) ? a : default;
            var name = StringProperty(labels, "alertname") ?? "(unnamed)";
            if (HeartbeatAlerts.Contains(name)) continue;

            // v2 has no per-alert "state" field worth passing on beyond status.state, which after
            // the filters above is always "active": every alert here is firing and routed.
            var state = alert.TryGetProperty("status", out var status)
                        && status.TryGetProperty("state", out var s)
                ? s.GetString() ?? "active"
                : "active";

            DateTime? since = null;
            if (alert.TryGetProperty("startsAt", out var startsAt)
                && DateTime.TryParse(
                    startsAt.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                since = parsed;
            }

            results.Add(new PlatformAlert(
                Name: name,
                Severity: StringProperty(labels, "severity") ?? "unknown",
                State: state == "active" ? "firing" : state,
                Summary: StringProperty(annotations, "summary") ?? StringProperty(annotations, "description"),
                ActiveSince: since));
        }

        return results;
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.GetString()
            : null;
}

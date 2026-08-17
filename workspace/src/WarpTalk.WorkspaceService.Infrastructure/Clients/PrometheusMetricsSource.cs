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
/// Reads the platform metrics store over the Prometheus HTTP API.
///
/// GET only, and only the two read endpoints. Prometheus also exposes admin routes that delete
/// series and shut the process down; they are not reachable through this type, and the deployed
/// server does not run with <c>--web.enable-admin-api</c>.
/// </summary>
public class PrometheusMetricsSource : IPlatformMetricsSource
{
    private readonly HttpClient _http;

    public PrometheusMetricsSource(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<PlatformMetricSample>> QueryAsync(
        string expression,
        CancellationToken ct)
    {
        var document = await GetAsync($"api/v1/query?query={Uri.EscapeDataString(expression)}", ct);
        var root = document.RootElement;

        RequireSuccess(root, expression);

        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var samples = new List<PlatformMetricSample>(result.GetArrayLength());
        foreach (var entry in result.EnumerateArray())
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (entry.TryGetProperty("metric", out var metric)
                && metric.ValueKind == JsonValueKind.Object)
            {
                foreach (var label in metric.EnumerateObject())
                {
                    labels[label.Name] = label.Value.GetString() ?? string.Empty;
                }
            }

            if (!entry.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Array
                || value.GetArrayLength() < 2)
            {
                continue;
            }

            // [ <unix seconds, number>, "<sample value, STRING>" ]. The value is a string in this
            // API precisely so it can carry NaN and +Inf, which JSON numbers cannot — and
            // histogram_quantile returns NaN for a window with too few observations.
            var raw = value[1].GetString();
            samples.Add(new PlatformMetricSample(labels, ParseSample(raw)));
        }

        return samples;
    }

    public async Task<IReadOnlyList<PlatformAlert>> ActiveAlertsAsync(CancellationToken ct)
    {
        var document = await GetAsync("api/v1/alerts", ct);
        var root = document.RootElement;

        RequireSuccess(root, "alerts");

        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("alerts", out var alerts)
            || alerts.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<PlatformAlert>(alerts.GetArrayLength());
        foreach (var alert in alerts.EnumerateArray())
        {
            var labels = alert.TryGetProperty("labels", out var l) ? l : default;
            var annotations = alert.TryGetProperty("annotations", out var a) ? a : default;

            DateTime? activeSince = null;
            if (alert.TryGetProperty("activeAt", out var activeAt)
                && DateTime.TryParse(
                    activeAt.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                activeSince = parsed;
            }

            results.Add(new PlatformAlert(
                Name: StringProperty(labels, "alertname") ?? "(unnamed)",
                Severity: StringProperty(labels, "severity") ?? "unknown",
                State: alert.TryGetProperty("state", out var state) ? state.GetString() ?? "unknown" : "unknown",
                Summary: StringProperty(annotations, "summary"),
                ActiveSince: activeSince));
        }

        return results;
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(path, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new PlatformMetricsUnavailableException(
                "The metrics store could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PlatformMetricsUnavailableException(
                "The metrics store did not respond in time.", ex);
        }

        using (response)
        {
            // 4xx from Prometheus is a bad query and belongs to the caller; 5xx and the rest mean
            // the store itself is not serving, which is the unavailable case.
            if ((int)response.StatusCode >= 500)
            {
                throw new PlatformMetricsUnavailableException(
                    $"The metrics store answered {(int)response.StatusCode}.");
            }

            var document = await response.Content.ReadFromJsonAsync<JsonDocument>(ct);
            return document ?? throw new InvalidOperationException(
                "The metrics store returned an empty body.");
        }
    }

    private static void RequireSuccess(JsonElement root, string what)
    {
        if (!root.TryGetProperty("status", out var status)
            || !string.Equals(status.GetString(), "success", StringComparison.Ordinal))
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            throw new InvalidOperationException(
                $"The metrics store rejected '{what}': {error ?? "no reason given"}");
        }
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.GetString()
            : null;

    /// <summary>
    /// InvariantCulture explicitly. The value arrives as "0.006575"; parsed under a locale whose
    /// decimal separator is a comma that becomes 6575, which is the same class of bug that has
    /// already reached this codebase once through billing JSON.
    /// </summary>
    private static double ParseSample(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return double.NaN;
        if (string.Equals(raw, "NaN", StringComparison.OrdinalIgnoreCase)) return double.NaN;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : double.NaN;
    }
}

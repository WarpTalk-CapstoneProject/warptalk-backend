using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>
/// Reads the statuspage.io v2 API (<c>/api/v2/status.json</c>, <c>/api/v2/incidents.json</c>), which
/// LiveKit (Atlassian Statuspage), OpenAI and Cartesia (incident.io, the compatible API) all serve —
/// verified 2026-09-25. Stripe's status page has no such API. Tolerant: a missing or null field
/// becomes a null or a skipped incident, never an exception.
/// </summary>
public static class StatusPageParser
{
    public sealed record PageStatus(string? Indicator, string? Description);

    public static PageStatus ParseStatus(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object)
        {
            return new PageStatus(null, null);
        }

        return new PageStatus(String(status, "indicator"), String(status, "description"));
    }

    /// <summary>
    /// Incidents with an id and a start (started_at, else created_at). A resolved or postmortem
    /// incident without resolved_at is dropped rather than shown as still open.
    /// </summary>
    public static IReadOnlyList<ProviderStatusIncident> ParseIncidents(string provider, string json)
    {
        var result = new List<ProviderStatusIncident>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("incidents", out var incidents) || incidents.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var incident in incidents.EnumerateArray())
        {
            var id = String(incident, "id");
            var started = Time(incident, "started_at") ?? Time(incident, "created_at");
            if (string.IsNullOrWhiteSpace(id) || started is null) continue;

            var status = String(incident, "status") ?? "unknown";
            var resolved = Time(incident, "resolved_at");
            if (resolved is null && status is "resolved" or "postmortem" or "completed") continue;

            result.Add(new ProviderStatusIncident
            {
                Provider = provider,
                ExternalId = Clip(id, 100),
                Name = Clip(String(incident, "name") ?? "(unnamed incident)", 500),
                Impact = Clip(String(incident, "impact") ?? "none", 20),
                Status = Clip(status, 40),
                StartedAt = started.Value,
                ResolvedAt = resolved,
                Url = String(incident, "shortlink") is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                    ? Clip(link, 500)
                    : null,
            });
        }

        return result;
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTime? Time(JsonElement element, string name)
        => String(element, name) is { } text
           && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}

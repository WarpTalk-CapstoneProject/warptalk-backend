using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;

namespace WarpTalk.WorkspaceService.Application.Helpers;

/// <summary>
/// The audit log as a spreadsheet: one row per entry, the same columns the screen shows, UTC
/// timestamps, and the before/after state as JSON.
/// </summary>
public static class AdminAuditCsv
{
    public static readonly string[] Header =
    [
        "id", "performed_at_utc", "result", "action", "source_service",
        "actor_id", "actor_name", "actor_email",
        "entity_type", "entity_id", "entity_key", "entity_label",
        "workspace_id", "workspace_name",
        "reason", "error_message", "request_id", "ip_address", "user_agent",
        "before", "after",
    ];

    public static byte[] Render(IEnumerable<AdminAuditLogEntryDto> entries)
    {
        var builder = new StringBuilder();
        AppendRow(builder, Header);
        foreach (var entry in entries)
        {
            AppendRow(builder,
            [
                entry.Id.ToString(),
                entry.PerformedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                entry.Result,
                entry.Action,
                entry.SourceService,
                entry.Actor.Id.ToString(),
                entry.Actor.Name,
                entry.Actor.Email,
                entry.Entity.Type,
                entry.Entity.Id?.ToString(),
                entry.Entity.Key,
                entry.Entity.Label,
                entry.Entity.WorkspaceId?.ToString(),
                entry.Entity.WorkspaceName,
                entry.Reason,
                entry.ErrorMessage,
                entry.Request.CorrelationId,
                entry.Request.IpAddress,
                entry.Request.UserAgent,
                Json(entry.BeforeSummary),
                Json(entry.AfterSummary),
            ]);
        }

        // A BOM so a spreadsheet opens Vietnamese names and reasons as UTF-8, not as mojibake.
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
    }

    /// <summary>
    /// RFC 4180 quoting, plus formula neutralising: a reason typed as <c>=HYPERLINK(...)</c> is
    /// data somebody wrote into a free-text box, and must open in a spreadsheet as that text rather
    /// than run (OWASP "CSV injection").
    /// </summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var text = value;
        if (text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        var needsQuotes = text.IndexOfAny([',', '"', '\n', '\r']) >= 0 || text != text.Trim();
        return needsQuotes ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    private static void AppendRow(StringBuilder builder, IReadOnlyList<string?> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(Cell(cells[i]));
        }

        builder.Append("\r\n");
    }

    private static string? Json(IReadOnlyDictionary<string, string?>? summary) =>
        summary is null || summary.Count == 0 ? null : JsonSerializer.Serialize(summary);
}

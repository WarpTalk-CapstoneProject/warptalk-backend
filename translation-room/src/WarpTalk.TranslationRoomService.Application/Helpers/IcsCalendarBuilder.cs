using System;
using System.Text;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// WT-14: builds a minimal, RFC 5545-valid single-VEVENT .ics document for a scheduled room.
/// Kept as a pure string builder (no I/O) so the format can be unit tested without EF/DB.
/// </summary>
public static class IcsCalendarBuilder
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(1);

    public static string Build(
        string uid,
        string title,
        string? description,
        DateTime scheduledAtUtc,
        string joinLink,
        TimeSpan? duration = null,
        DateTime? nowUtc = null)
    {
        var start = DateTime.SpecifyKind(scheduledAtUtc, DateTimeKind.Utc);
        var end = start + (duration ?? DefaultDuration);
        var stamp = DateTime.SpecifyKind(nowUtc ?? DateTime.UtcNow, DateTimeKind.Utc);

        var descriptionLine = string.IsNullOrWhiteSpace(description)
            ? $"Join link: {joinLink}"
            : $"{description}\\n\\nJoin link: {joinLink}";

        var sb = new StringBuilder();
        sb.Append("BEGIN:VCALENDAR\r\n");
        sb.Append("VERSION:2.0\r\n");
        sb.Append("PRODID:-//WarpTalk//Translation Room//EN\r\n");
        sb.Append("CALSCALE:GREGORIAN\r\n");
        sb.Append("METHOD:PUBLISH\r\n");
        sb.Append("BEGIN:VEVENT\r\n");
        sb.Append($"UID:{Escape(uid)}\r\n");
        sb.Append($"DTSTAMP:{FormatUtc(stamp)}\r\n");
        sb.Append($"DTSTART:{FormatUtc(start)}\r\n");
        sb.Append($"DTEND:{FormatUtc(end)}\r\n");
        sb.Append($"SUMMARY:{Escape(title)}\r\n");
        sb.Append($"DESCRIPTION:{Escape(descriptionLine)}\r\n");
        sb.Append($"URL:{Escape(joinLink)}\r\n");
        sb.Append("END:VEVENT\r\n");
        sb.Append("END:VCALENDAR\r\n");

        return sb.ToString();
    }

    private static string FormatUtc(DateTime value) => value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");

    /// <summary>RFC 5545 §3.3.11 TEXT escaping — backslash, semicolon, comma, then newline.</summary>
    private static string Escape(string value) =>
        value
            .Replace("\\", "\\\\")
            .Replace(";", "\\;")
            .Replace(",", "\\,")
            .Replace("\r\n", "\\n")
            .Replace("\n", "\\n");
}

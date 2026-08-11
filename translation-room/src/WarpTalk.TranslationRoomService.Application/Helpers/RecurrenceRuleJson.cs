using System.Collections.Generic;
using System.Text.Json;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// WT-327: the one reader and writer of <c>translation_room_series.recurrence_by_weekdays</c>.
///
/// The column is a jsonb array because the schema was written before WEEKLY existed, so every
/// caller that wants the weekdays has to parse it. Two callers parsing it two ways is how one of
/// them ends up trusting an unsorted list, or throwing on a malformed blob inside a background
/// sweep that serves every other series in the workspace. So: one place, and it never throws.
/// </summary>
public static class RecurrenceRuleJson
{
    /// <summary>
    /// The stored ISO weekdays, normalised (de-duplicated, ascending, out-of-range dropped), or
    /// null when the column is empty or unreadable.
    ///
    /// A malformed blob reads as null, which every caller already treats as "the weekday the
    /// series starts on" — the rule the row was created with in every case the column has not
    /// been hand-edited. That is a strictly better outcome than a 500 on the meetings list.
    /// </summary>
    public static int[]? ReadWeekdays(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return RecurrenceScheduleCalculator.NormalizeWeekdays(JsonSerializer.Deserialize<List<int>>(json));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The column value for a resolved weekday list, or null for a cadence that has none.</summary>
    public static string? WriteWeekdays(IReadOnlyList<int>? weekdays) =>
        weekdays is { Count: > 0 } ? JsonSerializer.Serialize(weekdays) : null;
}

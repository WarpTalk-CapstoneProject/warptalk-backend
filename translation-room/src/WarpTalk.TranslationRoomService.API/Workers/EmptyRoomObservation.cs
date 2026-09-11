using StackExchange.Redis;
using System;
using System.Globalization;
using System.Threading.Tasks;

namespace WarpTalk.TranslationRoomService.API.Workers;

/// <summary>
/// The "first seen empty at" timestamp both reapers keep per room, so that "empty for N minutes"
/// is measured from an observation rather than guessed from participant columns. No column
/// records when a room became empty; see AbandonedRoomPolicy for why the grace needs one.
///
/// Each reaper keeps its own key, so the two graces stay independent of each other.
/// </summary>
internal static class EmptyRoomObservation
{
    public static async Task<DateTime?> ReadAsync(IDatabase db, string key)
    {
        var stored = await db.StringGetAsync(key);
        return stored.HasValue
            && DateTime.TryParse(
                stored.ToString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed)
                ? parsed
                : null;
    }

    /// <summary>
    /// The TTL runs comfortably past the grace, so nothing accumulates and a lost key costs at most
    /// one extra grace period rather than the room.
    /// </summary>
    public static Task StartAsync(IDatabase db, string key, DateTime now, TimeSpan grace) =>
        db.StringSetAsync(
            key,
            now.ToString("O", CultureInfo.InvariantCulture),
            grace + TimeSpan.FromHours(1));

    public static Task ClearAsync(IDatabase db, string key) => db.KeyDeleteAsync(key);
}

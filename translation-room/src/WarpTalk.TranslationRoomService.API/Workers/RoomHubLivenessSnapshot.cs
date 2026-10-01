using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.API.Workers;

/// <summary>
/// Which rooms the Gateway says still have a live hub socket, read once per reaper pass — and the
/// rule for when that outranks the participant rows.
///
/// A CONNECTED row is cleared by exactly one event, participant-offline on Redis pub/sub, and when
/// that event is lost the row says "in the room" forever. Both reapers counted it as a person, so a
/// room whose disconnect never arrived could never be ended; see <see cref="RoomHubLiveness"/>.
///
/// The rows are overruled only when all of these hold, and believed otherwise:
///   * the Gateway heartbeat has been running unbroken for <see cref="RoomHubLiveness.Warmup"/>
///     (an older Gateway, a Redis flush or a dead heartbeat would make every room look deserted);
///   * no Gateway replica asserts a socket on this room;
///   * the room started — or, never started, was created — inside <see cref="AbandonedRoomPolicy.Lookback"/>.
///     Older rooms are a backlog for a deliberate repair, not for a background sweep: ending them
///     here would stamp a month-long meeting and republish its artifacts as if it had just ended.
///
/// Any failure to read Redis falls back to the rows. Being wrong in that direction costs a room
/// that stays open; being wrong in the other ends somebody's meeting.
/// </summary>
internal sealed class RoomHubLivenessSnapshot
{
    private static readonly RoomHubLivenessSnapshot Untrusted = new(trusted: false, new HashSet<Guid>());

    private readonly bool _trusted;
    private readonly HashSet<Guid> _roomsWithSocket;

    private RoomHubLivenessSnapshot(bool trusted, HashSet<Guid> roomsWithSocket)
    {
        _trusted = trusted;
        _roomsWithSocket = roomsWithSocket;
    }

    public static async Task<RoomHubLivenessSnapshot> ReadAsync(
        IDatabase db,
        IReadOnlyCollection<Guid> roomIds,
        DateTime now,
        ILogger logger)
    {
        try
        {
            var marker = await db.StringGetAsync(RoomHubLiveness.HeartbeatKey);
            if (!marker.HasValue || !long.TryParse(marker.ToString(), out var sinceMs))
                return Untrusted;

            var beatingSince = DateTimeOffset.FromUnixTimeMilliseconds(sinceMs).UtcDateTime;
            if (now - beatingSince < RoomHubLiveness.Warmup)
                return Untrusted;

            var ids = roomIds.ToArray();
            if (ids.Length == 0)
                return new RoomHubLivenessSnapshot(trusted: true, new HashSet<Guid>());

            var values = await db.StringGetAsync(
                ids.Select(id => (RedisKey)RoomHubLiveness.RoomKey(id)).ToArray());

            var withSocket = new HashSet<Guid>();
            for (var i = 0; i < ids.Length; i++)
            {
                if (values[i].HasValue) withSocket.Add(ids[i]);
            }

            return new RoomHubLivenessSnapshot(trusted: true, withSocket);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read room hub liveness; trusting participant rows for this pass.");
            return Untrusted;
        }
    }

    /// <summary>
    /// The people in <paramref name="room"/>: <paramref name="peopleByRows"/>, unless the rows are
    /// overruled as described on the class, in which case nobody.
    /// </summary>
    public int PeopleIn(TranslationRoom room, int peopleByRows, DateTime now)
    {
        if (peopleByRows == 0 || !_trusted || _roomsWithSocket.Contains(room.Id))
            return peopleByRows;

        var since = room.StartedAt ?? room.CreatedAt;
        return now - since < AbandonedRoomPolicy.Lookback ? 0 : peopleByRows;
    }
}

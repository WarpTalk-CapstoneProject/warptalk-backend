using System;

namespace WarpTalk.Shared;

/// <summary>
/// "Does anybody still hold a hub socket on this room?" — written by the Gateway, read by the
/// abandoned-room reapers in TranslationRoomService, and the one place the keys and their timing
/// are written down.
///
/// WHY THE REAPERS NEED IT
///     A participant row reads CONNECTED from the REST join until exactly one event clears it: the
///     Gateway's OnDisconnectedAsync publishing participant-offline on Redis pub/sub. That event is
///     lost whenever the Gateway pod dies without running the handler, the room service is
///     restarting when it is published, or the client registered over REST and never opened a
///     socket at all. Nothing reconciles the row afterwards, and both reapers read CONNECTED as
///     "somebody is in here" — so one lost message kept a room IN_PROGRESS forever. Production had
///     six rooms from August still live in October.
///
///     The Gateway is the only process that knows which sockets are alive, so it says so here, and a
///     CONNECTED row on a room nobody holds a socket on stops counting as a person.
///
/// THE SHAPE
///     Each Gateway replica SETs <see cref="RoomKey(Guid)"/> for every room it holds a connection
///     on, every <see cref="BeatInterval"/>, with a <see cref="Ttl"/> of several beats. Replicas
///     never delete each other's assertions: a room key exists while ANY replica has a socket on the
///     room and expires on its own once none does — including when the replica that held it was
///     killed, which is the case pub/sub cannot cover.
///
///     The ABSENCE of a room key only means something if the Gateway is beating at all. An older
///     Gateway build, a Redis flush, or a heartbeat loop that is down would make every room look
///     deserted. So every beat also refreshes <see cref="HeartbeatKey"/>, whose value is the moment
///     beating began (SET NX, then refreshed), and a reader trusts a missing room key only once that
///     marker has existed for <see cref="Warmup"/>. Without the marker the reapers fall back to the
///     participant rows, exactly as before this existed.
/// </summary>
public static class RoomHubLiveness
{
    /// <summary>How often each Gateway replica re-asserts the rooms it holds sockets on.</summary>
    public static readonly TimeSpan BeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Four beats. A slow or missed beat does not make an occupied room read as deserted, and a room
    /// whose last socket died is known to be deserted within two minutes.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long the heartbeat must have been running before a missing room key is believed.
    ///
    /// Covers the first rollout, when replicas still running the older build hold sockets they do
    /// not report, and the minute after a Redis flush, when the room keys are rebuilding. Ten
    /// minutes is longer than a Gateway rollout and still short next to "forever".
    /// </summary>
    public static readonly TimeSpan Warmup = TimeSpan.FromMinutes(10);

    /// <summary>Unix milliseconds of the first beat of the current unbroken run of beats.</summary>
    public const string HeartbeatKey = "translationRoom:hub_liveness:since";

    /// <summary>Present while some Gateway replica holds a hub connection on the room.</summary>
    public static string RoomKey(Guid roomId) => RoomKey(roomId.ToString());

    /// <inheritdoc cref="RoomKey(Guid)"/>
    public static string RoomKey(string roomId) => $"translationRoom:{Normalize(roomId)}:hub_alive";

    /// <summary>
    /// The hub keys rooms by the Guid string the client sent, which .NET formats lowercase; the
    /// reader formats its own Guid. Normalising here keeps the two sides on one spelling.
    /// </summary>
    private static string Normalize(string roomId) =>
        Guid.TryParse(roomId, out var parsed) ? parsed.ToString() : roomId.Trim().ToLowerInvariant();
}

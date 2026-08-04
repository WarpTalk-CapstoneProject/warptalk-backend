using System.Text.Json.Serialization;

namespace WarpTalk.Gateway.Presence;

/// <summary>
/// What a workspace member is doing right now, as far as the Gateway can tell.
///
/// Derived entirely from live hub connections — there is no self-set status. Offline is never
/// stored: it is simply the absence of a presence record, so a crashed Gateway that never runs
/// OnDisconnectedAsync degrades to "offline" once the record's TTL lapses rather than leaving
/// someone online forever.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PresenceState
{
    Offline,
    Online,
    InMeeting
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace WarpTalk.TranslationRoomService.Domain.Constants;

/// <summary>
/// The kinds of meeting a room can be. INSTANT and SCHEDULED are the original two and stay
/// valid — 40 production rooms already carry them — but they are no longer what the UI
/// offers: they now behave as aliases for EVENT, which has the same neutral defaults.
/// </summary>
public static class TranslationRoomTypes
{
    public const string Event = "EVENT";
    public const string ChannelMeeting = "CHANNEL_MEETING";
    public const string Webinar = "WEBINAR";
    public const string CompanyMeeting = "COMPANY_MEETING";
    public const string VirtualAppointment = "VIRTUAL_APPOINTMENT";
    public const string LiveEvent = "LIVE_EVENT";

    /// <summary>
    /// The meeting happens somewhere else — Google Meet, Zoom, Teams — and WarpTalk only
    /// translates for it. The room holds exactly two participants: the WarpTalk user, and one
    /// pseudo-participant standing in for everyone on the far side of the external call. That
    /// pairing is what makes the existing audio-route mesh produce the two routes the bridge
    /// needs, without the AI pipeline knowing this room is any different.
    /// </summary>
    public const string ExternalBridge = "EXTERNAL_BRIDGE";

    /// <summary>Pre-existing values. Kept accepted so old rooms and old clients keep working.</summary>
    public const string LegacyInstant = "INSTANT";
    public const string LegacySchedule = "SCHEDULED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Event, ChannelMeeting, Webinar, CompanyMeeting, VirtualAppointment, LiveEvent,
        ExternalBridge,
        LegacyInstant, LegacySchedule,
    };

    public static bool IsKnown(string? type) =>
        !string.IsNullOrWhiteSpace(type) && All.Contains(type!);

    public static bool IsExternalBridge(string? type) =>
        string.Equals(type, ExternalBridge, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normalizes free-form input ("Channel Meeting", "channel-meeting") to the stored form.
    /// Returns null when unrecognised, so callers can reject rather than silently invent a type.
    /// </summary>
    public static string? Normalize(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;

        var candidate = new string(type.Trim().Select(c => c is ' ' or '-' ? '_' : char.ToUpperInvariant(c)).ToArray());
        return All.Contains(candidate) ? All.First(known => known.Equals(candidate, StringComparison.OrdinalIgnoreCase)) : null;
    }
}

/// <summary>
/// What a meeting type implies when a room is created. These are DEFAULTS, not constraints:
/// an explicit value in the create request always wins, and the host can change any of them
/// afterwards. Their whole purpose is that picking "Webinar" should not leave the room
/// configured identically to "Event", which is what happened while the type was cosmetic.
/// </summary>
public sealed record TranslationRoomTypeDefaults(
    bool RequiresApproval,
    bool MuteOnEntry,
    bool AutoRecord,
    bool BreakoutsEnabled,
    int MaxParticipants);

public static class TranslationRoomTypePolicy
{
    private static readonly TranslationRoomTypeDefaults Neutral =
        new(RequiresApproval: false, MuteOnEntry: false, AutoRecord: false, BreakoutsEnabled: true, MaxParticipants: 100);

    private static readonly Dictionary<string, TranslationRoomTypeDefaults> ByType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [TranslationRoomTypes.Event] = Neutral,
            [TranslationRoomTypes.LegacyInstant] = Neutral,
            [TranslationRoomTypes.LegacySchedule] = Neutral,

            // Team-internal and smaller than a general event.
            [TranslationRoomTypes.ChannelMeeting] =
                new(RequiresApproval: false, MuteOnEntry: false, AutoRecord: false, BreakoutsEnabled: true, MaxParticipants: 50),

            // Audience-style: vet who gets in, keep them muted, keep a recording.
            [TranslationRoomTypes.Webinar] =
                new(RequiresApproval: true, MuteOnEntry: true, AutoRecord: true, BreakoutsEnabled: false, MaxParticipants: 500),

            // All-hands: everyone in the workspace is expected, so no lobby, but muted and recorded.
            [TranslationRoomTypes.CompanyMeeting] =
                new(RequiresApproval: false, MuteOnEntry: true, AutoRecord: true, BreakoutsEnabled: true, MaxParticipants: 500),

            // Strictly 1:1 — the second seat is the guest, so breakouts are meaningless.
            [TranslationRoomTypes.VirtualAppointment] =
                new(RequiresApproval: true, MuteOnEntry: false, AutoRecord: false, BreakoutsEnabled: false, MaxParticipants: 2),

            // Broadcast to a large audience.
            [TranslationRoomTypes.LiveEvent] =
                new(RequiresApproval: true, MuteOnEntry: true, AutoRecord: true, BreakoutsEnabled: false, MaxParticipants: 1000),

            // Exactly two seats and nothing else can join: the WarpTalk user, and the
            // pseudo-participant that stands in for the external call. A lobby would have
            // nobody to admit and breakouts nowhere to break out to. Auto-record stays off
            // because the far side never agreed to anything — see the voice-clone rule that
            // travels with this type.
            [TranslationRoomTypes.ExternalBridge] =
                new(RequiresApproval: false, MuteOnEntry: false, AutoRecord: false, BreakoutsEnabled: false, MaxParticipants: 2),
        };

    /// <summary>Falls back to the neutral profile for an unknown type rather than throwing —
    /// validation rejects unknown types at the edge, so this only guards older stored rows.</summary>
    public static TranslationRoomTypeDefaults For(string? type) =>
        type != null && ByType.TryGetValue(type, out var defaults) ? defaults : Neutral;
}

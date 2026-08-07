using System.Text.Json.Serialization;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.Domain.ValueObjects;

public class TranslationRoomSettings
{
    [JsonPropertyName("requires_approval")]
    public bool RequiresApproval { get; set; } = true;

    /// <summary>
    /// Who besides the host may reach the room's artifacts. Values come from
    /// <see cref="WarpTalk.TranslationRoomService.Domain.Constants.ArtifactAccessLevels"/> and are
    /// validated on the way in by the create and settings-update paths — this stayed a bare string
    /// with no shared vocabulary long enough for the guard reading it to compare against spellings
    /// no writer ever produced.
    /// </summary>
    [JsonPropertyName("artifact_access")]
    public string ArtifactAccess { get; set; } = ArtifactAccessLevels.HostOnly;

    // The three below are seeded from the meeting type at creation (see
    // TranslationRoomTypePolicy). They live here rather than in a column because this is
    // already a jsonb settings blob — no migration, and they read alongside the two settings
    // that were always here.
    //
    // They are the room's DEFAULT stance. The live host controls in meeting-service
    // (SetMuteOnEntry, SetRecording, breakout endpoints) still override moment to moment;
    // these say what the room starts out as, which the type is what decides.

    /// <summary>Whether joiners land with their microphone off.</summary>
    [JsonPropertyName("mute_on_entry")]
    public bool MuteOnEntry { get; set; }

    /// <summary>Whether recording should begin on its own when the meeting starts.</summary>
    [JsonPropertyName("auto_record")]
    public bool AutoRecord { get; set; }

    /// <summary>Whether breakout rooms are offered at all for this meeting.</summary>
    [JsonPropertyName("breakouts_enabled")]
    public bool BreakoutsEnabled { get; set; } = true;
}

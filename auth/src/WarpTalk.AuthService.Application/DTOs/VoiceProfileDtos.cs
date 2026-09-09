using System;
using Microsoft.AspNetCore.Http;

namespace WarpTalk.AuthService.Application.DTOs;

public record VoiceSampleDto(
    Guid Id,
    string SampleType,
    int? DurationSeconds,
    string? Language,
    DateTime CreatedAt
);

public record VoiceProfileDto(
    Guid Id,
    string? DisplayName,
    string? Language,
    string Status,
    bool IsActive,
    bool HasSample,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    // Which TTS provider this profile points at ("cartesia"), and that provider's own id for
    // the voice. For a picked library voice, ProviderVoiceId is exactly the id the client
    // round-trips into TranslationRoomHub.SetVoicePreference — the client needs it back out,
    // which is why these are exposed rather than kept internal.
    string? Provider = null,
    string? ProviderVoiceId = null,
    string? ConsentStatus = null,
    string? ConsentTextVersion = null,
    DateTime? ConsentGrantedAt = null,
    /// <summary>
    /// What this row IS — see VoiceProfileSources. "upload" and "in_meeting" are voices of this
    /// person's; "library" is their pick of a public catalogue voice, kept in the same table.
    ///
    /// Exposed because the client cannot work it out from anything else here. Provider is
    /// "cartesia" for a pick and for an upload that has finished cloning, so a page filtering on
    /// provider listed somebody's catalogue pointer among their own recordings and showed them
    /// its provider id as their chosen voice.
    /// </summary>
    string? Source = null
);

/// <summary>One selectable voice from the provider's public library.</summary>
public record VoiceCatalogItemDto(
    string Id,
    string Name,
    string Gender
);

/// <summary>
/// Pick (or clear) the library voice this user hears for one language. VoiceId null/empty
/// clears the preference and falls back to the automatic per-speaker default.
/// </summary>
public record SetPreferredVoiceRequest(
    string Language,
    string? VoiceId
);

/// <summary>
/// WT-396 — pick (or clear) the voice this user is DUBBED IN.
///
/// The opposite direction from SetPreferredVoiceRequest above, and the distinction is the bug
/// this exists for: that one says which voice you HEAR everybody else in, this one says how YOU
/// sound to them. They shared a table, so an upload meant to change how someone sounded changed
/// nothing at all.
///
/// VoiceId null or empty clears the choice and returns to cloning the speaker live from the
/// meeting, which is what happens for everyone who has not chosen.
///
/// Language is only used to validate a catalogue pick; a voice belonging to one of the user's own
/// profiles is accepted regardless, because it is theirs.
/// </summary>
public record SetDubVoiceRequest(
    string? VoiceId,
    string? Language = null
);

/// <summary>
/// Ask to hear a voice before a meeting rather than during one.
///
/// The language is required and is not a formality: a voice is previewed by SPEAKING in that
/// language, and the same voice is a different judgement in Vietnamese than in English — which
/// is the gap between the "80% in English, 70% in Vietnamese" the feature exists to make
/// measurable.
/// </summary>
public record PreviewVoiceRequest(
    string? VoiceId,
    string? Language
);

public class CreateVoiceProfileRequest
{
    public string DisplayName { get; set; } = null!;
    public string Language { get; set; } = null!;
    public IFormFile? Sample { get; set; }
    public bool OwnVoiceConfirmed { get; set; }
    public bool AiUseConfirmed { get; set; }
    public bool SyntheticVoiceAcknowledged { get; set; }
    public bool NoImpersonationConfirmed { get; set; }
    public bool RetentionAcknowledged { get; set; }
}

/// <summary>
/// An uploaded recording on its way back to the person who uploaded it.
///
/// The bytes are read into memory rather than streamed: a sample is capped at 20 MB on the way in
/// (VoiceProfileService.MaxSampleSizeBytes) and the caller is one person pressing play, so the
/// simplicity is worth more than the streaming.
/// </summary>
public sealed record VoiceSampleContent(byte[] Content, string ContentType, string FileName);

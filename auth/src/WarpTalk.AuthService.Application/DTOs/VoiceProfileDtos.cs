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
    string? ProviderVoiceId = null
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

public class CreateVoiceProfileRequest
{
    public string DisplayName { get; set; } = null!;
    public string Language { get; set; } = null!;
    public IFormFile? Sample { get; set; }
}

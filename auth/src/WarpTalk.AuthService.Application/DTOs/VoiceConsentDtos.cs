using System;

namespace WarpTalk.AuthService.Application.DTOs;

/// <summary>
/// What this person has decided about voice cloning.
///
/// `HasDecided` is separate from `IsGranted` on purpose. "Never been asked" and "asked and said
/// no" are the same false to a boolean and completely different to a user interface: one should
/// show the consent prompt, the other must not nag someone who has already declined.
/// </summary>
public record VoiceConsentStatusDto(
    bool HasDecided,
    bool IsGranted,
    string? Status,
    string? ConsentTextVersion,
    DateTime? GrantedAt,
    DateTime? RevokedAt);

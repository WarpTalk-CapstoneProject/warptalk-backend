using System;

namespace WarpTalk.AuthService.Application.DTOs;

public record UserIdentityDto(
    Guid Id,
    string Email,
    string FullName,
    string? AvatarUrl,
    string? PreferredLanguage
);

public record UserLanguageDefaultsDto(
    string DefaultSpeakLanguage,
    string DefaultListenLanguage,
    // WT-401. Not a language, and the record's name is now half a lie — kept anyway, because
    // renaming it would touch every caller for no behavioural gain, and this is the one RPC that
    // already reads UserSetting. What it carries is the user's WISH to be dubbed in their own
    // voice; the permission to do so is HasVoiceCloneConsent and stays separate.
    bool VoiceCloneEnabled
);

public record RoleDto(
    Guid Id,
    string Name,
    string? Description
);

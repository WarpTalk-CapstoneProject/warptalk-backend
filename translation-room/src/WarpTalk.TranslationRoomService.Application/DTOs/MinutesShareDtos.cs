using System;
using System.Collections.Generic;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// The share dialog's state: the link, who it lets in, and who has been named.
///
/// <paramref name="Url"/> is built server-side from the configured frontend base so that every
/// surface — the web app, an email, a notification — sends people to the same address. A client
/// assembling it from the token would be one deploy away from sending half the recipients to
/// localhost.
/// </summary>
public record MinutesShareDto(
    string Token,
    string Url,
    string AccessMode,
    bool AllowDownload,
    DateTime? ExpiresAt,
    DateTime? RevokedAt,
    IReadOnlyList<MinutesSharePersonDto> People);

public record MinutesSharePersonDto(string Email, DateTime CreatedAt);

/// <summary>
/// A change to the sharing state. Every field is optional and null means "leave as it is", so the
/// share dialog can toggle downloads without restating the access mode it did not touch.
/// </summary>
public record UpdateMinutesShareRequest(
    string? AccessMode = null,
    bool? AllowDownload = null,
    DateTime? ExpiresAt = null);

public record AddMinutesSharePersonRequest(string Email);

/// <summary>
/// What somebody holding a link is shown.
///
/// The minutes, and the two facts the page needs to render honestly: whether a download button
/// belongs on it, and how they got in. It deliberately carries no room, no participant list and
/// no transcript — reading a shared biên bản is reading that document and nothing else.
/// </summary>
public record SharedMinutesDto(
    MeetingMinutesDto Minutes,
    string AccessMode,
    bool AllowDownload);

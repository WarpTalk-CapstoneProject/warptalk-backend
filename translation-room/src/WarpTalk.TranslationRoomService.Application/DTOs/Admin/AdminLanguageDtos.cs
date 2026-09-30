namespace WarpTalk.TranslationRoomService.Application.DTOs.Admin;

/// <summary>
/// One catalog row as the admin portal sees it (WT-691), with how many unfinished meetings use it.
///
/// The catalog has exactly one switch — <paramref name="IsActive"/>. There are no per-capability
/// (STT / MT / TTS) flags in <c>translation_room.supported_languages</c>, and nothing downstream
/// would read one, so none is offered here.
/// </summary>
/// <param name="LiveMeetings">Rooms IN_PROGRESS, PAUSED or WAITING whose source or target is this language.</param>
/// <param name="UpcomingMeetings">SCHEDULED rooms whose source or target is this language.</param>
public record AdminLanguageDto(
    string Code,
    string Name,
    string? NativeName,
    bool IsActive,
    int LiveMeetings,
    int UpcomingMeetings);

/// <summary><c>POST /api/v1/admin/languages</c>. <c>IsActive</c> defaults to true.</summary>
public record AdminCreateLanguageRequest(
    string? Code,
    string? Name,
    string? NativeName,
    bool? IsActive = null);

/// <summary><c>PUT /api/v1/admin/languages/{code}</c>. The code is the key and cannot change.</summary>
public record AdminUpdateLanguageRequest(
    string? Name,
    string? NativeName);

/// <summary>
/// <c>POST /api/v1/admin/languages/{code}/disable</c>. Disabling a language that scheduled
/// meetings still use is refused unless <paramref name="ConfirmUpcoming"/> is true; one that a live
/// meeting uses is refused outright.
/// </summary>
public record AdminDisableLanguageRequest(bool ConfirmUpcoming = false);

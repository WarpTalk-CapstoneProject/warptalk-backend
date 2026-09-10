using System;
using System.Collections.Generic;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// This biên bản's proceedings in a language the document does not already carry.
///
/// WHY THIS IS NOT JUST ANOTHER ENTRY IN <c>MeetingMinutesContent.Translations</c>.
///     That map is produced when the summary is written, and it covers the ROOM's target
///     languages — the ones the meeting was actually being interpreted into. A person who reads
///     Japanese and joined a Vietnamese/English room is not in that set and never will be, so
///     for them the document has no language at all. This is the answer for exactly that reader,
///     and it is generated on request rather than in advance: the alternative is translating
///     every meeting into every language nobody asked for.
///
/// <c>Status</c> is the part a caller must handle. The first person to ask for a language pays
/// for a model call and gets <c>generating</c>; everybody after them gets it immediately.
/// </summary>
/// <param name="Language">Bare ISO 639-1 of the requested language.</param>
/// <param name="Sections">The proceedings in that language, or null while still being written.</param>
/// <param name="Status">ready | generating | unavailable.</param>
/// <param name="UnavailableReason">
/// Why this document cannot be translated, in words for the person who asked. Set only with
/// <c>unavailable</c> — a refusal a reader cannot act on is the same as a bug to them.
/// </param>
public record MinutesTranslationDto(
    string Language,
    List<MinutesSection>? Sections,
    string Status,
    string? UnavailableReason);

public static class MinutesTranslationStatus
{
    /// <summary>The sections are in this response.</summary>
    public const string Ready = "ready";

    /// <summary>Queued. The caller refetches; nothing is wrong.</summary>
    public const string Generating = "generating";

    /// <summary>
    /// This document cannot honestly be translated. Not an error and not a failure — a state
    /// with a reason the reader can act on, which is why it carries one.
    /// </summary>
    public const string Unavailable = "unavailable";
}

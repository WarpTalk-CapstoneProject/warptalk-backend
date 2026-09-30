using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// WT-703: which languages NEW artifact content (a summary rendering, a summary regenerate, a
/// minutes translation) may be generated in for one room.
///
/// Languages narrow as they go down: the workspace whitelist (L1) bounds what a meeting may
/// declare (L2), and the meeting's declared set bounds what its shared artifacts may be produced
/// in (L3). Without this gate a finished meeting held in Vietnamese and English could have its
/// summary rendered in any language the platform supports, by anyone who can read the record —
/// an owner's workspace policy held for the live call and then leaked out through the artifacts.
///
/// Only GENERATION is gated. Content that already exists is never re-filtered here: a policy
/// tightened after the fact must not make an already-published summary unreadable.
/// </summary>
public interface IRoomArtifactLanguagePolicy
{
    /// <summary>
    /// The room's generatable languages: its source plus its targets (normalized, deduped, source
    /// first), intersected with the workspace whitelist when that whitelist is non-empty, and with
    /// the active platform catalog.
    ///
    /// Fail modes are chosen so the answer never widens past the room and never collapses to
    /// nothing because a dependency blinked:
    /// <list type="bullet">
    /// <item>Workspace lookup fails, throws, or the room has no workspace → the room's own set.
    /// That set was already validated against the whitelist when the room was created or edited,
    /// so it is the last known-good answer, not a bypass.</item>
    /// <item>Catalog read throws or returns no active rows → the catalog filter is skipped. An
    /// empty catalog is a data problem, and refusing every generation over it would hide that
    /// problem behind a confusing "language not allowed".</item>
    /// </list>
    /// </summary>
    Task<IReadOnlyList<string>> GetGeneratableLanguagesAsync(
        TranslationRoom room,
        CancellationToken ct = default);

    /// <summary>
    /// Success when <paramref name="requestedLanguage"/> may be generated for this room.
    ///
    /// Null, empty or whitespace means "as spoken" — follow the transcript's own languages — and
    /// is always allowed. Anything else is normalized with
    /// <see cref="Helpers.LanguageHelper.NormalizeLanguageCode"/> and must be a bare ISO-639 code
    /// (two or three letters) inside <see cref="GetGeneratableLanguagesAsync"/>; free text such as
    /// "klingon" or a prompt fragment is refused before it can reach a model.
    /// </summary>
    /// <returns>
    /// A <see cref="ErrorCodes.ValidationError"/> failure (HTTP 400 in every controller mapping)
    /// whose message names the requested language and the allowed set. <see cref="Result"/>
    /// carries a single code, so the machine reason "LANGUAGE_NOT_ALLOWED" is not a separate
    /// field; callers branch on the validation code.
    /// </returns>
    Task<Result> EnsureCanGenerateAsync(
        TranslationRoom room,
        string? requestedLanguage,
        CancellationToken ct = default);
}

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using WarpTalk.Shared;

namespace WarpTalk.WorkspaceService.Application.Helpers;

/// <summary>
/// The one reading of the workspace language whitelist (WT-706): is the workspace restricted at
/// all, and what does a stored code look like.
///
/// It exists because the three places that touched the list disagreed. The workspace service
/// compared raw strings case-insensitively, while translation-room and the gateway compared
/// primary subtags — so a workspace that saved "vi-VN" could create no room at all, since rooms
/// store "vi". Normalizing HERE, on the way in, is what makes the comparison agree everywhere:
/// what is stored is already what the other services compare against.
/// </summary>
public static class WorkspaceLanguagePolicy
{
    /// <summary>
    /// A well-formed primary subtag: two or three letters, ISO-639-1 or -639-3, already
    /// lower-cased by <see cref="LanguageTag.Base"/>.
    ///
    /// SHAPE, NOT EXISTENCE, and that is a known limit rather than a choice. The platform's
    /// catalogue is <c>translation_room.supported_languages</c> — owned by translation-room,
    /// reachable through its admin REST endpoint and not exposed on translation_room.proto at
    /// all. The workspace service has no client for it and inventing one would put a settings
    /// save behind another service's availability, so an unknown-but-well-formed code such as
    /// "xx" is accepted here and refused later by the room-creation path, which does read the
    /// catalogue. Add a catalogue RPC and this is the single place that has to change.
    /// </summary>
    private static readonly Regex WellFormedPrimarySubtag =
        new("^[a-z]{2,3}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether the workspace restricts target languages.
    ///
    /// <paramref name="restrictLanguages"/> null means the caller did not say — an old client
    /// sending back a settings document it read before WT-706, or a stored document written
    /// before it. Both are then read the way every reader used to read them: a non-empty list is
    /// a restriction, an empty one is not.
    /// </summary>
    public static bool IsRestricted(bool? restrictLanguages, IEnumerable<string>? allowedTargetLanguages)
    {
        return restrictLanguages
            ?? allowedTargetLanguages?.Any(code => !string.IsNullOrWhiteSpace(code))
            ?? false;
    }

    /// <summary>
    /// Trims, reduces each entry to its lower-case primary subtag, drops blanks and dedupes,
    /// preserving the order the caller sent. The first entry that is not a well-formed code stops
    /// the walk and is reported as-written, so the refusal can name what the owner actually typed
    /// rather than what it reduced to.
    /// </summary>
    public static WorkspaceLanguageNormalizationResult Normalize(IEnumerable<string>? codes)
    {
        var normalized = new List<string>();
        if (codes == null)
        {
            return new WorkspaceLanguageNormalizationResult(normalized, null);
        }

        foreach (var raw in codes)
        {
            // A blank entry is not an error — a checkbox list that serializes a trailing empty
            // slot is not the owner naming a language, so it is dropped rather than refused.
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var code = LanguageTag.Base(raw);
            if (!WellFormedPrimarySubtag.IsMatch(code))
            {
                return new WorkspaceLanguageNormalizationResult(normalized, raw.Trim());
            }

            if (!normalized.Contains(code))
            {
                normalized.Add(code);
            }
        }

        return new WorkspaceLanguageNormalizationResult(normalized, null);
    }
}

/// <param name="Codes">The accepted codes, normalized and deduped. Partial when a code was rejected.</param>
/// <param name="OffendingCode">The code that stopped the walk, exactly as the caller wrote it. Null when every entry was accepted.</param>
public sealed record WorkspaceLanguageNormalizationResult(List<string> Codes, string? OffendingCode)
{
    public bool IsValid => OffendingCode is null;
}

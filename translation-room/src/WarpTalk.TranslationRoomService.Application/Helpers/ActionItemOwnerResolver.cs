using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// Turning "anh Tú said he'd write the release note" into a row somebody can be assigned.
///
/// WHY THE MODEL IS NOT ASKED FOR AN ID
///     It would need the participant roster in its prompt, which means another cross-service call
///     from the AI worker and a second place where a stale roster produces a confident wrong
///     answer. The roster is authoritative HERE, at draft time, so the model keeps doing what it
///     is good at — reporting the name that was said — and the matching happens where the truth is.
///
/// WHY MATCHING IS DELIBERATELY CONSERVATIVE
///     An unresolved owner is a line that reads "Nhi" and cannot be assigned. A WRONG resolution
///     is a task assigned to a colleague who never agreed to it, sitting in their list with the
///     meeting's authority behind it. The two failures are not symmetrical, so this refuses on any
///     ambiguity and never guesses.
///
/// HOW A SPOKEN NAME MEETS A DISPLAY NAME
///     People are called by one part of their name in a meeting — "Tú", "Nhi" — while the roster
///     holds "Huỳnh Thái Tú". So a match is: every word of the spoken name appears in the display
///     name, or the other way round. Diacritics are NOT folded away: in Vietnamese they are the
///     difference between words, and folding them would make "Tú" and "Tu" the same person.
/// </summary>
public static class ActionItemOwnerResolver
{
    /// <summary>Honorifics and address terms that carry no identity.</summary>
    private static readonly HashSet<string> AddressTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "anh", "chị", "chi", "em", "bạn", "ban", "cô", "co", "thầy", "thay", "ông", "ong", "bà", "ba",
        "mr", "mrs", "ms", "miss", "dr"
    };

    /// <summary>
    /// The participant this owner name refers to, or null when nobody or more than one does.
    ///
    /// Null is the safe answer and the common one: the meeting may name somebody who was not in
    /// the room, or nobody at all.
    /// </summary>
    public static TranslationRoomParticipant? Resolve(
        string? ownerName, IReadOnlyCollection<TranslationRoomParticipant> participants)
    {
        var spoken = Words(ownerName);
        if (spoken.Count == 0) return null;

        var matches = participants
            .Where(participant => Matches(spoken, Words(participant.DisplayName)))
            .ToList();

        // Exactly one, or nothing. Two people a name could mean is precisely the case where a
        // guess assigns work to the wrong person.
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool Matches(IReadOnlyCollection<string> spoken, IReadOnlyCollection<string> display)
    {
        if (display.Count == 0) return false;

        // Containment either way: "Tú" identifies "Huỳnh Thái Tú", and a model that helpfully
        // wrote the full name identifies a roster holding only "Tú".
        return spoken.All(display.Contains) || display.All(spoken.Contains);
    }

    /// <summary>
    /// The identifying words of a name: lower-cased, punctuation removed, address terms dropped.
    ///
    /// Diacritics survive on purpose — see the class remarks.
    /// </summary>
    private static List<string> Words(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new List<string>();

        var builder = new StringBuilder(name.Length);
        foreach (var character in name.Normalize(NormalizationForm.FormC))
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLower(character, CultureInfo.InvariantCulture) : ' ');
        }

        var words = builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !AddressTerms.Contains(word))
            .ToList();

        // A name that is ONLY an address term — "anh", "chị" — identifies nobody, and dropping
        // every word would otherwise leave an empty set that matches the first person tried.
        return words;
    }
}

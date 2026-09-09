using System.Collections.Generic;
using System.Linq;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// Deciding when a translated line may be printed next to the original line it translates.
///
/// THE PROBLEM
///     The translated sections come from a model asked for "the same shape, translated". Nothing
///     makes the two arrays the same length, the same order, or a one-to-one mapping — and this is
///     a signed document, so a line printed beside the wrong original is a decision attributed to
///     something nobody decided. Position is therefore never evidence of correspondence.
///
/// THE JOIN KEY IS THE CITATION
///     Every summary item is required to carry `atMs`, the moment in the meeting it came from.
///     Two items citing the same moment are two renderings of the same thing — which is a real
///     join, not a guess. So: pair on `atMs`, never on index.
///
/// ALL OR NOTHING, PER SECTION
///     Pairing is used for a section only when EVERY original item in it matches exactly one
///     translated item. A section where half the lines pair and half do not would print in two
///     layouts at once, and a reader would have to work out which lines were claims of
///     correspondence and which were not. When the rule does not hold, the caller prints the
///     translated section whole, underneath — which asserts nothing about any individual line.
/// </summary>
public static class MinutesBilingualPairing
{
    /// <summary>One original line and its translation, when there is one.</summary>
    public readonly record struct Pair(MinutesItem Original, MinutesItem Translated);

    /// <summary>
    /// The line-by-line pairing for a section, or null when it cannot be established — in which
    /// case the translation belongs in a block of its own.
    /// </summary>
    public static List<Pair>? PairByCitation(
        IReadOnlyList<MinutesItem>? original, IReadOnlyList<MinutesItem>? translated)
    {
        if (original == null || translated == null) return null;
        if (original.Count == 0 || translated.Count == 0) return null;

        // A citation only joins if it is unambiguous on BOTH sides. Two originals citing the same
        // moment cannot be told apart by it, and neither can two translations.
        if (original.Any(item => item.AtMs == null)) return null;
        if (original.Select(item => item.AtMs!.Value).Distinct().Count() != original.Count) return null;

        var byCitation = new Dictionary<long, MinutesItem>();
        foreach (var item in translated)
        {
            if (item.AtMs == null) continue;
            if (!byCitation.TryAdd(item.AtMs.Value, item)) return null;
        }

        var pairs = new List<Pair>(original.Count);
        foreach (var item in original)
        {
            if (!byCitation.TryGetValue(item.AtMs!.Value, out var match)) return null;
            pairs.Add(new Pair(item, match));
        }

        return pairs;
    }

    /// <summary>The translated counterpart of one section, by key, or null when there is none.</summary>
    public static MinutesSection? CounterpartOf(
        MinutesSection section, IReadOnlyList<MinutesSection>? translatedSections)
    {
        return translatedSections?.FirstOrDefault(candidate => candidate.Key == section.Key);
    }
}

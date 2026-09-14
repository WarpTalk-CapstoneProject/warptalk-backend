using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// WT-685 — one biên bản, read in ONE language.
///
/// WHAT WAS WRONG
///     The writers printed every stored translation under every section, choosing per section
///     the first language (alphabetically) that happened to pair. One file could carry [ja] under
///     3.1 and [vi] under 3.2, and a "translation" into the record's own language printed English
///     under English with an [en] tag.
///
/// WHAT THIS DOES INSTEAD
///     Shapes the content BEFORE a writer sees it, so both layouts get the same rule without
///     either being rewritten:
///       * no language, or the original's own — the original and nothing else;
///       * mono (the default) — every section replaced by its counterpart in that language, and a
///         section with none says so rather than borrowing another language;
///       * bilingual — the original plus exactly that one language, never more.
///
///     Carried-over items are quotations of an earlier meeting's record and stay as written.
/// </summary>
public static class MinutesLanguageView
{
    public const string ModeMono = "mono";
    public const string ModeBilingual = "bilingual";

    public static string UntranslatedNotice(string language) =>
        $"This section has not been translated into {language} yet.";

    /// <summary>
    /// The stored translations with each key reduced to its bare code ("vi-VN" → "vi"), the
    /// original's own language removed, and the first spelling of a language kept. Null when
    /// nothing is left.
    /// </summary>
    public static Dictionary<string, List<MinutesSection>>? CleanTranslations(
        IReadOnlyDictionary<string, List<MinutesSection>>? translations, string? primaryLanguage)
    {
        if (translations == null || translations.Count == 0) return null;

        var original = LanguageHelper.NormalizeLanguageCode(primaryLanguage);
        var cleaned = new Dictionary<string, List<MinutesSection>>(StringComparer.Ordinal);

        foreach (var pair in translations.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var code = LanguageHelper.NormalizeLanguageCode(pair.Key);
            if (code.Length == 0 || code == original || pair.Value is not { Count: > 0 }) continue;
            cleaned.TryAdd(code, pair.Value);
        }

        return cleaned.Count > 0 ? cleaned : null;
    }

    /// <summary>
    /// The content as it should be rendered for <paramref name="language"/> in
    /// <paramref name="mode"/>. <paramref name="requested"/> is a reading generated on request
    /// for a language the record does not store; it wins over a stored one. Never mutates the
    /// input.
    /// </summary>
    public static MeetingMinutesContent Shape(
        MeetingMinutesContent content,
        string? language,
        string? mode,
        List<MinutesSection>? requested = null)
    {
        var shaped = Clone(content);
        var original = LanguageHelper.NormalizeLanguageCode(content.PrimaryLanguage);
        var wanted = LanguageHelper.NormalizeLanguageCode(language);

        if (wanted.Length == 0 || wanted == original)
        {
            shaped.Translations = null;
            return shaped;
        }

        var cleaned = CleanTranslations(content.Translations, content.PrimaryLanguage);
        List<MinutesSection>? translated = requested is { Count: > 0 }
            ? requested
            : cleaned != null && cleaned.TryGetValue(wanted, out var stored) ? stored : null;

        if (string.Equals(mode, ModeBilingual, StringComparison.OrdinalIgnoreCase))
        {
            shaped.Translations = translated == null
                ? null
                : new Dictionary<string, List<MinutesSection>> { [wanted] = translated };
            return shaped;
        }

        shaped.Sections = (content.Sections ?? new List<MinutesSection>())
            .Select(section => ReadIn(section, translated, wanted))
            .ToList();
        shaped.Translations = null;
        return shaped;
    }

    private static MinutesSection ReadIn(
        MinutesSection section, List<MinutesSection>? translated, string language)
    {
        if (section.Key == MeetingMinutesDrafter.CarriedOverKey) return section;

        var counterpart = MinutesBilingualPairing.CounterpartOf(section, translated);
        if (counterpart != null && HasWords(counterpart)) return counterpart;

        return new MinutesSection
        {
            Key = section.Key,
            Kind = "paragraph",
            Text = UntranslatedNotice(language)
        };
    }

    private static bool HasWords(MinutesSection section) =>
        !string.IsNullOrWhiteSpace(section.Text)
        || (section.Items?.Any(item => !string.IsNullOrWhiteSpace(item.Text)) ?? false);

    private static MeetingMinutesContent Clone(MeetingMinutesContent content) =>
        JsonSerializer.Deserialize<MeetingMinutesContent>(JsonSerializer.Serialize(content))!;
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace WarpTalk.TranscriptService.Application.GlossaryImportTemplates;

/// <summary>
/// WT-880: what an admin may save as the import template, and the one normalization both sides
/// apply to a header cell. Pure, so it is tested without a database.
/// </summary>
public static partial class GlossaryImportTemplateRules
{
    public const int MaxNameLength = 60;
    public const int MaxAliasLength = 60;
    public const int MaxAliasesPerColumn = 20;
    public const int MaxSampleLength = 300;
    public const int MaxSampleLanguages = 100;

    [GeneratedRegex(@"^[a-z]{2,3}$")]
    private static partial Regex LanguageCodePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s*\([^()]*\)\s*$")]
    private static partial Regex TrailingParenthetical();

    /// <summary>
    /// How a header cell is compared: trimmed, lowercased, inner whitespace collapsed, and one
    /// trailing "(…)" dropped — the template writes "Term (English)" / "Translation (Vietnamese)",
    /// and the importer must read that as "term" / "translation". Mirrors normalizeHeader in the
    /// web's src/lib/glossary/import-template.ts.
    /// </summary>
    public static string NormalizeHeader(string? value)
    {
        var text = Whitespace().Replace((value ?? string.Empty).Trim(), " ").ToLowerInvariant();
        var stripped = TrailingParenthetical().Replace(text, string.Empty).Trim();
        // "(English)" on its own is not a name; keep what was there rather than an empty key.
        return stripped.Length == 0 ? text : stripped;
    }

    /// <summary><c>en-US</c>, <c>EN</c> and <c>en_us</c> are all <c>en</c>.</summary>
    public static string NormalizeLanguage(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant().Split('-', '_')[0];

    /// <summary>
    /// Validates a PUT body and returns the configuration as it will be stored (trimmed, aliases
    /// de-duplicated, order renumbered 0..n-1 within each group, empty samples dropped), or the
    /// first problem found.
    /// </summary>
    public static (GlossaryImportTemplateConfig? Config, string? Error) Validate(UpdateGlossaryImportTemplateDto? request)
    {
        if (request?.Columns is null)
            return (null, "columns is required.");

        var columns = new List<GlossaryImportTemplateColumnDto>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (raw, index) in request.Columns.Select((c, i) => (c, i)))
        {
            if (raw is null) return (null, $"columns[{index}] is empty.");
            var key = raw.Key?.Trim() ?? string.Empty;
            if (!GlossaryImportTemplateColumnKeys.All.Contains(key))
                return (null, $"Unknown column key '{key}'. Expected one of: {string.Join(", ", GlossaryImportTemplateColumnKeys.All)}.");
            if (!seenKeys.Add(key))
                return (null, $"Column '{key}' appears twice.");

            var group = raw.Group?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!GlossaryImportTemplateGroups.All.Contains(group))
                return (null, $"Column '{key}' has unknown group '{raw.Group}'. Expected source, target or general.");

            // The importer cannot read a file without these two, and a term is a source-language
            // word and a translation a target-language one by definition.
            if (key == GlossaryImportTemplateColumnKeys.SourceTerm && group != GlossaryImportTemplateGroups.Source)
                return (null, "The Term column must stay in the Source group.");
            if (key == GlossaryImportTemplateColumnKeys.TargetTerm && group != GlossaryImportTemplateGroups.Target)
                return (null, "The Translation column must stay in the Target group.");
            if (raw.Hidden && (key == GlossaryImportTemplateColumnKeys.SourceTerm || key == GlossaryImportTemplateColumnKeys.TargetTerm))
                return (null, "The Term and Translation columns cannot be hidden: the importer needs both.");

            var name = Whitespace().Replace(raw.Name?.Trim() ?? string.Empty, " ");
            if (name.Length == 0)
                return (null, $"Column '{key}' needs a name.");
            if (name.Length > MaxNameLength)
                return (null, $"Column '{key}' name is longer than {MaxNameLength} characters.");

            var aliases = new List<string>();
            var aliasKeys = new HashSet<string>(StringComparer.Ordinal) { NormalizeHeader(name) };
            foreach (var alias in raw.Aliases ?? [])
            {
                var text = Whitespace().Replace(alias?.Trim() ?? string.Empty, " ");
                if (text.Length == 0) continue;
                if (text.Length > MaxAliasLength)
                    return (null, $"Column '{key}' has an alias longer than {MaxAliasLength} characters.");
                if (aliasKeys.Add(NormalizeHeader(text))) aliases.Add(text);
            }
            if (aliases.Count > MaxAliasesPerColumn)
                return (null, $"Column '{key}' has more than {MaxAliasesPerColumn} aliases.");

            columns.Add(new GlossaryImportTemplateColumnDto(key, group, raw.Order, raw.Hidden, name, aliases));
        }

        var missing = GlossaryImportTemplateColumnKeys.All.Where(k => !seenKeys.Contains(k)).ToList();
        if (missing.Count > 0)
            return (null, $"Missing column(s): {string.Join(", ", missing)}.");

        // One header text, one meaning. Two columns answering to "note" would make the importer
        // pick whichever came first in the file.
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            foreach (var label in column.Aliases.Prepend(column.Name))
            {
                var normalized = NormalizeHeader(label);
                if (owner.TryGetValue(normalized, out var other) && other != column.Key)
                    return (null, $"'{label}' is used by both '{other}' and '{column.Key}'. A header name or alias may belong to one column only.");
                owner[normalized] = column.Key;
            }
        }

        var ordered = Order(columns);

        var samples = new SortedDictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (rawCode, values) in request.Samples ?? [])
        {
            var code = NormalizeLanguage(rawCode);
            if (!LanguageCodePattern().IsMatch(code))
                return (null, $"'{rawCode}' is not a language code (expected a bare ISO-639 code such as en, vi, ja).");
            if (samples.ContainsKey(code))
                return (null, $"Samples for '{code}' are given twice.");

            var row = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (rawKey, rawValue) in values ?? [])
            {
                var key = rawKey?.Trim() ?? string.Empty;
                if (!GlossaryImportTemplateColumnKeys.All.Contains(key))
                    return (null, $"Samples for '{code}' name unknown column '{rawKey}'.");
                var value = rawValue?.Trim() ?? string.Empty;
                if (value.Length == 0) continue;
                if (value.Length > MaxSampleLength)
                    return (null, $"Sample '{key}' for '{code}' is longer than {MaxSampleLength} characters.");
                if (key == GlossaryImportTemplateColumnKeys.Priority
                    && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    return (null, $"Sample priority for '{code}' must be a whole number.");
                row[key] = value;
            }
            if (row.Count > 0) samples[code] = row;
        }
        if (samples.Count > MaxSampleLanguages)
            return (null, $"Samples are limited to {MaxSampleLanguages} languages.");

        return (new GlossaryImportTemplateConfig(ordered, samples), null);
    }

    /// <summary>Source, Target, General; within a group by Order then the given position; renumbered from 0.</summary>
    public static IReadOnlyList<GlossaryImportTemplateColumnDto> Order(IEnumerable<GlossaryImportTemplateColumnDto> columns)
    {
        return columns
            .Select((column, position) => (column, position))
            .GroupBy(x => x.column.Group)
            .OrderBy(g => Array.IndexOf(GlossaryImportTemplateGroups.All, g.Key))
            .SelectMany(g => g
                .OrderBy(x => x.column.Order)
                .ThenBy(x => x.position)
                .Select((x, i) => x.column with { Order = i }))
            .ToList();
    }
}

/// <summary>The stored document (also what the DTO is built from).</summary>
public sealed record GlossaryImportTemplateConfig(
    IReadOnlyList<GlossaryImportTemplateColumnDto> Columns,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Samples);

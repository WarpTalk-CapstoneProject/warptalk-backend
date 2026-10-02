using System.Collections.Generic;

namespace WarpTalk.TranscriptService.Application.GlossaryImportTemplates;

/// <summary>
/// WT-880: what the import template is before any admin has touched it — and again after "Reset
/// to default". Served whenever <c>transcript.glossary_import_template</c> holds no row, so the
/// template works with zero admin action and no data migration.
///
/// Groups (PO decisions 2026-10-02): Source = Term, Context, Part of speech; Target = Translation,
/// Definition, Note; General = Field, Priority. Definition is in Target because it is written in
/// the target language: "Bug" in an EN→JA glossary is defined in Japanese, in an EN→EN glossary
/// in English. The header names are the ones every earlier
/// template and sample file used, and the aliases are the importer's historical ones, so a file
/// made before this change still imports.
///
/// The web keeps an identical copy (src/lib/glossary/import-template.ts, DEFAULT_IMPORT_TEMPLATE)
/// as its offline fallback; change both together.
///
/// Sample values describe ONE concept in each language, the PO's own example: an engineering
/// meeting where "Bug" must reach a Japanese listener as 不具合, not 虫 (insect). Any pair lines
/// up: the Source group shows the source language's word and sentence, the Target group the target
/// language's word, definition and note. They are skipped by the importer.
/// </summary>
public static class GlossaryImportTemplateDefaults
{
    public static IReadOnlyList<GlossaryImportTemplateColumnDto> Columns { get; } =
    [
        new(GlossaryImportTemplateColumnKeys.SourceTerm, GlossaryImportTemplateGroups.Source, 0, false, "Term",
            ["source term", "sourceterm", "source"]),
        new(GlossaryImportTemplateColumnKeys.Context, GlossaryImportTemplateGroups.Source, 1, false, "Context",
            ["usage context", "context sentence", "example", "example sentence", "ngữ cảnh", "ngu canh", "câu ví dụ", "ví dụ"]),
        new(GlossaryImportTemplateColumnKeys.PartOfSpeech, GlossaryImportTemplateGroups.Source, 2, false, "Part of speech",
            ["partofspeech", "pos"]),
        new(GlossaryImportTemplateColumnKeys.TargetTerm, GlossaryImportTemplateGroups.Target, 0, false, "Translation",
            ["target term", "targetterm", "target", "translate as"]),
        new(GlossaryImportTemplateColumnKeys.Definition, GlossaryImportTemplateGroups.Target, 1, false, "Definition",
            ["meaning"]),
        new(GlossaryImportTemplateColumnKeys.UsageNote, GlossaryImportTemplateGroups.Target, 2, false, "Note",
            ["usage note", "usagenote"]),
        new(GlossaryImportTemplateColumnKeys.Domain, GlossaryImportTemplateGroups.General, 0, false, "Field",
            ["domain", "business domain"]),
        new(GlossaryImportTemplateColumnKeys.Priority, GlossaryImportTemplateGroups.General, 1, false, "Priority",
            []),
    ];

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Samples { get; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["en"] = new Dictionary<string, string>
            {
                [GlossaryImportTemplateColumnKeys.SourceTerm] = "Bug",
                [GlossaryImportTemplateColumnKeys.Context] = "We need to fix this bug before the Sprint 14 release.",
                [GlossaryImportTemplateColumnKeys.PartOfSpeech] = "noun",
                [GlossaryImportTemplateColumnKeys.TargetTerm] = "Bug",
                [GlossaryImportTemplateColumnKeys.Definition] = "A defect in software that causes wrong behaviour",
                [GlossaryImportTemplateColumnKeys.UsageNote] = "Used in engineering meetings",
                [GlossaryImportTemplateColumnKeys.Domain] = "Software engineering",
                [GlossaryImportTemplateColumnKeys.Priority] = "5",
            },
            ["vi"] = new Dictionary<string, string>
            {
                [GlossaryImportTemplateColumnKeys.SourceTerm] = "lỗi phần mềm",
                [GlossaryImportTemplateColumnKeys.Context] = "Chúng ta cần fix gấp Bug này trước khi release Sprint 14.",
                [GlossaryImportTemplateColumnKeys.PartOfSpeech] = "danh từ",
                [GlossaryImportTemplateColumnKeys.TargetTerm] = "lỗi phần mềm",
                [GlossaryImportTemplateColumnKeys.Definition] = "Sai sót trong phần mềm khiến chương trình chạy sai",
                [GlossaryImportTemplateColumnKeys.UsageNote] = "Kỹ sư thường nói tắt là \"bug\"",
                [GlossaryImportTemplateColumnKeys.Domain] = "Kỹ thuật phần mềm",
                [GlossaryImportTemplateColumnKeys.Priority] = "5",
            },
            ["ja"] = new Dictionary<string, string>
            {
                [GlossaryImportTemplateColumnKeys.SourceTerm] = "不具合",
                [GlossaryImportTemplateColumnKeys.Context] = "リリース前にこの不具合を修正する必要があります。",
                [GlossaryImportTemplateColumnKeys.PartOfSpeech] = "名詞",
                [GlossaryImportTemplateColumnKeys.TargetTerm] = "不具合",
                [GlossaryImportTemplateColumnKeys.Definition] = "ソフトウェアの欠陥や誤動作",
                [GlossaryImportTemplateColumnKeys.UsageNote] = "「ソフトウェア障害」とも言う",
                [GlossaryImportTemplateColumnKeys.Domain] = "ソフトウェア工学",
                [GlossaryImportTemplateColumnKeys.Priority] = "5",
            },
        };
}

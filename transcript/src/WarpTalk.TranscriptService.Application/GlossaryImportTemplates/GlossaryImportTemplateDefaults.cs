using System.Collections.Generic;

namespace WarpTalk.TranscriptService.Application.GlossaryImportTemplates;

/// <summary>
/// WT-880: what the import template is before any admin has touched it — and again after "Reset
/// to default". Served whenever <c>transcript.glossary_import_template</c> holds no row, so the
/// template works with zero admin action and no data migration.
///
/// Groups (PO decision 2026-10-02): Source = Term, Context, Part of speech; Target = Translation,
/// Note; General = Field, Definition, Priority. The header names are the ones every earlier
/// template and sample file used, and the aliases are the importer's historical ones, so a file
/// made before this change still imports.
///
/// The web keeps an identical copy (src/lib/glossary/import-template.ts, DEFAULT_IMPORT_TEMPLATE)
/// as its offline fallback; change both together.
///
/// Sample values describe ONE concept ("invoice") in each language, so any pair lines up: the
/// Source group shows the source language's word and sentence, the Target group the target
/// language's word. They are not domain vocabulary and are skipped by the importer.
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
        new(GlossaryImportTemplateColumnKeys.UsageNote, GlossaryImportTemplateGroups.Target, 1, false, "Note",
            ["usage note", "usagenote"]),
        new(GlossaryImportTemplateColumnKeys.Domain, GlossaryImportTemplateGroups.General, 0, false, "Field",
            ["domain", "business domain"]),
        new(GlossaryImportTemplateColumnKeys.Definition, GlossaryImportTemplateGroups.General, 1, false, "Definition",
            ["meaning"]),
        new(GlossaryImportTemplateColumnKeys.Priority, GlossaryImportTemplateGroups.General, 2, false, "Priority",
            []),
    ];

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Samples { get; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["en"] = new Dictionary<string, string>
            {
                [GlossaryImportTemplateColumnKeys.SourceTerm] = "invoice",
                [GlossaryImportTemplateColumnKeys.Context] = "Please send the invoice before Friday.",
                [GlossaryImportTemplateColumnKeys.PartOfSpeech] = "noun",
                [GlossaryImportTemplateColumnKeys.TargetTerm] = "invoice",
                [GlossaryImportTemplateColumnKeys.UsageNote] = "Use in billing and payment talk",
                [GlossaryImportTemplateColumnKeys.Domain] = "Finance",
                [GlossaryImportTemplateColumnKeys.Definition] = "A document listing goods or services and the amount due",
                [GlossaryImportTemplateColumnKeys.Priority] = "5",
            },
            ["vi"] = new Dictionary<string, string>
            {
                [GlossaryImportTemplateColumnKeys.SourceTerm] = "hóa đơn",
                [GlossaryImportTemplateColumnKeys.Context] = "Vui lòng gửi hóa đơn trước thứ Sáu.",
                [GlossaryImportTemplateColumnKeys.PartOfSpeech] = "danh từ",
                [GlossaryImportTemplateColumnKeys.TargetTerm] = "hóa đơn",
                [GlossaryImportTemplateColumnKeys.UsageNote] = "Dùng khi nói về thanh toán",
                [GlossaryImportTemplateColumnKeys.Domain] = "Tài chính",
                [GlossaryImportTemplateColumnKeys.Definition] = "Chứng từ ghi hàng hóa, dịch vụ và số tiền phải trả",
                [GlossaryImportTemplateColumnKeys.Priority] = "5",
            },
            ["ja"] = new Dictionary<string, string>
            {
                [GlossaryImportTemplateColumnKeys.SourceTerm] = "請求書",
                [GlossaryImportTemplateColumnKeys.Context] = "金曜日までに請求書を送ってください。",
                [GlossaryImportTemplateColumnKeys.PartOfSpeech] = "名詞",
                [GlossaryImportTemplateColumnKeys.TargetTerm] = "請求書",
                [GlossaryImportTemplateColumnKeys.UsageNote] = "支払いの話で使う",
                [GlossaryImportTemplateColumnKeys.Domain] = "財務",
                [GlossaryImportTemplateColumnKeys.Definition] = "商品やサービスと支払金額を記載した書類",
                [GlossaryImportTemplateColumnKeys.Priority] = "5",
            },
        };
}

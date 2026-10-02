using System;

namespace WarpTalk.TranscriptService.Domain.Entities;

/// <summary>
/// WT-880: the platform admin's configuration of the glossary import FILE SHAPE — columns, their
/// Source / Target / General group, order, hidden flag, header name, aliases, and per-language
/// sample values. Singleton row (<see cref="SingletonId"/>); no row means the built-in default.
/// See migration 20261002090000_add_glossary_import_template.
/// </summary>
public class GlossaryImportTemplate
{
    public const short SingletonId = 1;

    public short Id { get; set; } = SingletonId;

    /// <summary>The configuration document (jsonb). Shape: GlossaryImportTemplateConfig.</summary>
    public string Config { get; set; } = "{}";

    public DateTime UpdatedAt { get; set; }

    /// <summary>AuthService user id of the admin who last saved it. No physical FK.</summary>
    public Guid? UpdatedBy { get; set; }
}

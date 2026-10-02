using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Domain.Entities;

namespace WarpTalk.TranscriptService.Application.GlossaryImportTemplates;

/// <summary>
/// WT-880: the glossary import template is a FILE SHAPE, not a pack of terms. One configuration
/// for the whole platform; the web composes the file for a language pair from it:
/// <list type="bullet">
/// <item>Source group columns take the SOURCE language's sample values,</item>
/// <item>Target group columns take the TARGET language's sample values,</item>
/// <item>General group columns take the source language's sample values.</item>
/// </list>
/// Switching EN→VI to EN→JA therefore changes only the Target group.
/// </summary>
public static class GlossaryImportTemplateGroups
{
    public const string Source = "source";
    public const string Target = "target";
    public const string General = "general";

    /// <summary>In file order: Source, then Target, then General.</summary>
    public static readonly string[] All = [Source, Target, General];
}

/// <summary>
/// The columns a glossary import file can carry. Each key IS the importer's field name
/// (<c>ParsedGlossaryRow</c> on the web, <c>BulkImportGlossaryTermItemDto</c> here), so the
/// template and the importer cannot disagree about what a column means. Fixed set: the admin
/// configures how these appear, never adds a field <c>glossary_terms</c> has no column for.
/// </summary>
public static class GlossaryImportTemplateColumnKeys
{
    public const string SourceTerm = "sourceTerm";
    public const string Context = "context";
    public const string PartOfSpeech = "partOfSpeech";
    public const string TargetTerm = "targetTerm";
    public const string UsageNote = "usageNote";
    public const string Domain = "domain";
    public const string Definition = "definition";
    public const string Priority = "priority";

    public static readonly string[] All =
        [SourceTerm, Context, PartOfSpeech, TargetTerm, UsageNote, Domain, Definition, Priority];
}

public sealed record GlossaryImportTemplateColumnDto(
    string Key,
    string Group,
    int Order,
    bool Hidden,
    string Name,
    IReadOnlyList<string> Aliases);

/// <summary>
/// What both GET endpoints answer. <c>Samples</c>: language code (bare ISO-639, lowercase) →
/// column key → sample value. <c>IsDefault</c>: no admin has saved a configuration, so the
/// built-in default is served.
/// </summary>
public sealed record GlossaryImportTemplateDto(
    IReadOnlyList<GlossaryImportTemplateColumnDto> Columns,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Samples,
    bool IsDefault,
    DateTime? UpdatedAt,
    Guid? UpdatedBy);

/// <summary>PUT body: the whole configuration, replaced as one.</summary>
public sealed class UpdateGlossaryImportTemplateDto
{
    public List<UpdateGlossaryImportTemplateColumnDto>? Columns { get; set; }

    public Dictionary<string, Dictionary<string, string?>>? Samples { get; set; }
}

public sealed class UpdateGlossaryImportTemplateColumnDto
{
    public string? Key { get; set; }
    public string? Group { get; set; }
    public int Order { get; set; }
    public bool Hidden { get; set; }
    public string? Name { get; set; }
    public List<string?>? Aliases { get; set; }
}

public interface IGlossaryImportTemplateService
{
    /// <summary>The saved configuration, or the built-in default when none was saved.</summary>
    Task<Result<GlossaryImportTemplateDto>> GetAsync(CancellationToken ct = default);

    /// <summary>Validates and replaces the whole configuration.</summary>
    Task<Result<GlossaryImportTemplateDto>> UpdateAsync(
        UpdateGlossaryImportTemplateDto request, Guid actorId, CancellationToken ct = default);

    /// <summary>Drops the saved configuration; the built-in default is served again.</summary>
    Task<Result<GlossaryImportTemplateDto>> ResetAsync(CancellationToken ct = default);
}

/// <summary>Persistence of the singleton <see cref="GlossaryImportTemplate"/> row.</summary>
public interface IGlossaryImportTemplateStore
{
    Task<GlossaryImportTemplate?> GetAsync(CancellationToken ct = default);

    /// <summary>Inserts or replaces the singleton row and saves.</summary>
    Task<GlossaryImportTemplate> SaveAsync(string configJson, Guid actorId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>Deletes the singleton row (if any) and saves.</summary>
    Task DeleteAsync(CancellationToken ct = default);
}

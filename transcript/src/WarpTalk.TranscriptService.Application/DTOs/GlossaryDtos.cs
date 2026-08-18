using System;
using System.ComponentModel.DataAnnotations;

namespace WarpTalk.TranscriptService.Application.DTOs;

public record CreateGlossaryDto(
    [Required(ErrorMessage = "WorkspaceId is required.")]
    Guid WorkspaceId,

    [Required(ErrorMessage = "Name is required.")]
    [MaxLength(100, ErrorMessage = "Name cannot exceed 100 characters.")]
    string Name,

    [MaxLength(500, ErrorMessage = "Description cannot exceed 500 characters.")]
    string? Description,

    [Required(ErrorMessage = "SourceLanguage is required.")]
    [MaxLength(10, ErrorMessage = "SourceLanguage cannot exceed 10 characters.")]
    string SourceLanguage,

    [Required(ErrorMessage = "TargetLanguage is required.")]
    [MaxLength(10, ErrorMessage = "TargetLanguage cannot exceed 10 characters.")]
    string TargetLanguage
);

public record UpdateGlossaryDto(
    [Required(ErrorMessage = "Name is required.")]
    [MaxLength(100, ErrorMessage = "Name cannot exceed 100 characters.")]
    string Name,

    [MaxLength(500, ErrorMessage = "Description cannot exceed 500 characters.")]
    string? Description,

    [Required(ErrorMessage = "IsActive is required.")]
    bool IsActive
);

public record GlossaryDto(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    string? Description,
    string SourceLanguage,
    string TargetLanguage,
    int TermCount,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record CreateGlossaryTermDto(
    [Required(ErrorMessage = "SourceTerm is required.")]
    [MaxLength(200, ErrorMessage = "SourceTerm cannot exceed 200 characters.")]
    string SourceTerm,

    [Required(ErrorMessage = "TargetTerm is required.")]
    [MaxLength(200, ErrorMessage = "TargetTerm cannot exceed 200 characters.")]
    string TargetTerm,

    [MaxLength(1000, ErrorMessage = "Context cannot exceed 1000 characters.")]
    string? Context,

    [MaxLength(100, ErrorMessage = "Domain cannot exceed 100 characters.")]
    string? Domain,

    string? Definition,
    string? UsageNote,
    [MaxLength(50)] string? PartOfSpeech,

    [Range(0, 10, ErrorMessage = "Priority must be between 0 and 10.")]
    int Priority = 0
);

/// <summary>
/// WT-472: an Excel/CSV import, as one request.
///
/// Not a convenience wrapper over AddTerm. Adding a hundred terms one call at a time is a hundred
/// round trips, a hundred `SaveChangesAsync` calls and a hundred separate increments of
/// <c>Glossary.TermCount</c> — and a client that dies halfway leaves the counter describing a
/// glossary that does not exist. This settles the whole file once.
/// </summary>
public record BulkImportGlossaryTermsDto(
    [Required]
    [MinLength(1, ErrorMessage = "At least one term is required.")]
    [MaxLength(2000, ErrorMessage = "A single import may not exceed 2000 terms.")]
    IReadOnlyList<CreateGlossaryTermDto> Terms
);

/// <param name="Imported">Terms written.</param>
/// <param name="Skipped">
/// Rows the server declined — today only exact duplicates of a term already in this glossary.
/// Reported rather than silently dropped: an import that says "100 imported" when it wrote 60 is
/// how somebody comes to believe a term exists that does not.
/// </param>
/// <param name="Errors">One message per rejected row, in the order they arrived.</param>
public record BulkImportGlossaryTermsResultDto(
    int Imported,
    int Skipped,
    IReadOnlyList<string> Errors
);

public record UpdateGlossaryTermDto(
    [Required(ErrorMessage = "SourceTerm is required.")]
    [MaxLength(200, ErrorMessage = "SourceTerm cannot exceed 200 characters.")]
    string SourceTerm,

    [Required(ErrorMessage = "TargetTerm is required.")]
    [MaxLength(200, ErrorMessage = "TargetTerm cannot exceed 200 characters.")]
    string TargetTerm,

    [MaxLength(1000, ErrorMessage = "Context cannot exceed 1000 characters.")]
    string? Context,

    [MaxLength(100, ErrorMessage = "Domain cannot exceed 100 characters.")]
    string? Domain,

    string? Definition,
    string? UsageNote,
    [MaxLength(50)] string? PartOfSpeech,

    [Range(0, 10, ErrorMessage = "Priority must be between 0 and 10.")]
    int Priority,

    [Required(ErrorMessage = "IsActive is required.")]
    bool IsActive
);

public record GlossaryTermDto(
    Guid Id,
    Guid GlossaryId,
    string SourceTerm,
    string TargetTerm,
    string? Context,
    string? Domain,
    string? Definition,
    string? UsageNote,
    string? PartOfSpeech,
    int Priority,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

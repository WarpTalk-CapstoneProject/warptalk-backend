using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace WarpTalk.TranscriptService.Application.DTOs;

public record CreateGlobalGlossaryTermDto(
    [Required(ErrorMessage = "Term is required.")]
    [MinLength(3, ErrorMessage = "Term must be at least 3 characters — short/common words risk hijacking every meeting's STT (see docs/global-glossary-plan.md §6).")]
    [MaxLength(255, ErrorMessage = "Term cannot exceed 255 characters.")]
    string Term,

    [Required(ErrorMessage = "PreferredTranslation is required.")]
    [MaxLength(255, ErrorMessage = "PreferredTranslation cannot exceed 255 characters.")]
    string PreferredTranslation,

    [MaxLength(15)] string? SourceLanguage,
    [MaxLength(15)] string? TargetLanguage,
    [MaxLength(100)] string? BusinessDomain,
    string? Definition,
    string? UsageNote,

    [Range(0, 10, ErrorMessage = "Priority must be between 0 and 10.")]
    int Priority = 5
);

public record UpdateGlobalGlossaryTermDto(
    [Required(ErrorMessage = "Term is required.")]
    [MinLength(3, ErrorMessage = "Term must be at least 3 characters — short/common words risk hijacking every meeting's STT (see docs/global-glossary-plan.md §6).")]
    [MaxLength(255, ErrorMessage = "Term cannot exceed 255 characters.")]
    string Term,

    [Required(ErrorMessage = "PreferredTranslation is required.")]
    [MaxLength(255, ErrorMessage = "PreferredTranslation cannot exceed 255 characters.")]
    string PreferredTranslation,

    [MaxLength(15)] string? SourceLanguage,
    [MaxLength(15)] string? TargetLanguage,
    [MaxLength(100)] string? BusinessDomain,
    string? Definition,
    string? UsageNote,

    [Range(0, 10, ErrorMessage = "Priority must be between 0 and 10.")]
    int Priority
);

public record GlobalGlossaryTermDto(
    Guid Id,
    string Term,
    string PreferredTranslation,
    string? SourceLanguage,
    string? TargetLanguage,
    string? BusinessDomain,
    string? Definition,
    string? UsageNote,
    int Priority,
    string Status,
    int Version,
    DateTime CreatedAt,
    Guid? CreatedBy,
    DateTime UpdatedAt,
    Guid? UpdatedBy
);

public record GlobalGlossaryAuditDto(
    Guid Id,
    Guid TermId,
    string Action,
    string? BeforeJson,
    string? AfterJson,
    Guid ActorUserId,
    DateTime CreatedAt
);

public record PagedResultDto<T>(
    IEnumerable<T> Items,
    int Page,
    int PageSize,
    int TotalCount
);

public record GlobalGlossaryTermQuery(
    int Page = 1,
    int PageSize = 20,
    string? Status = null,
    string? BusinessDomain = null,
    string? Language = null,
    string? Search = null
);

public record BulkImportGlobalGlossaryTermRow(
    string Term,
    string PreferredTranslation,
    string? SourceLanguage,
    string? TargetLanguage,
    string? BusinessDomain,
    string? Definition,
    string? UsageNote,
    int Priority = 5
);

public record BulkImportGlobalGlossaryTermsDto(
    [Required] IReadOnlyList<BulkImportGlobalGlossaryTermRow> Rows
);

public record BulkImportResultDto(
    int Imported,
    int Skipped,
    IReadOnlyList<string> Errors
);

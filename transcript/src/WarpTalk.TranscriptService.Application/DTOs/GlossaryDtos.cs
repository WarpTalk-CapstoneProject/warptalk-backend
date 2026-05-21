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
    
    [Range(0, 10, ErrorMessage = "Priority must be between 0 and 10.")]
    int Priority = 0
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
    int Priority,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

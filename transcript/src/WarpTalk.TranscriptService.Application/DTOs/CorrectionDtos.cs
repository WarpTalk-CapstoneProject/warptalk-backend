using System;
using System.ComponentModel.DataAnnotations;
using WarpTalk.TranscriptService.Domain.Enums;

namespace WarpTalk.TranscriptService.Application.DTOs;

public record CreateCorrectionDto(
    [Required(ErrorMessage = "UserId is required.")] 
    Guid UserId,
    
    [Required(ErrorMessage = "OriginalText is required.")] 
    [MaxLength(2000, ErrorMessage = "OriginalText cannot exceed 2000 characters.")]
    string OriginalText,
    
    [Required(ErrorMessage = "CorrectedText is required.")] 
    [MaxLength(2000, ErrorMessage = "CorrectedText cannot exceed 2000 characters.")]
    string CorrectedText,
    
    [Required(ErrorMessage = "CorrectionType is required.")]
    string CorrectionType,

    /// <summary>Required when CorrectionType == "MT" — identifies which target-language translation is being corrected, so the correction record can link to the right transcript.translation_contents row.</summary>
    string? TargetLanguage = null
);

public record TranscriptCorrectionDto(
    Guid Id,
    Guid SegmentId,
    Guid UserId,
    string OriginalText,
    string CorrectedText,
    string CorrectionType,
    string Status,
    bool TriggeredRetranslation,
    Guid? ReviewedBy,
    DateTime? ReviewedAt,
    DateTime CreatedAt
);

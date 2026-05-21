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
    CorrectionType CorrectionType
);

public record TranscriptCorrectionDto(
    Guid Id,
    Guid SegmentId,
    Guid UserId,
    string OriginalText,
    string CorrectedText,
    CorrectionType CorrectionType,
    CorrectionStatus Status,
    bool TriggeredRetranslation,
    Guid? ReviewedBy,
    DateTime? ReviewedAt,
    DateTime CreatedAt
);

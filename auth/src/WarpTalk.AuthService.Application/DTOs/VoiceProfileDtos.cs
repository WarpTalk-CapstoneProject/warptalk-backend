using System;
using Microsoft.AspNetCore.Http;

namespace WarpTalk.AuthService.Application.DTOs;

public record VoiceSampleDto(
    Guid Id,
    string SampleType,
    int? DurationSeconds,
    string? Language,
    DateTime CreatedAt
);

public record VoiceProfileDto(
    Guid Id,
    string? DisplayName,
    string? Language,
    string Status,
    bool IsActive,
    bool HasSample,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public class CreateVoiceProfileRequest
{
    public string DisplayName { get; set; } = null!;
    public string Language { get; set; } = null!;
    public IFormFile? Sample { get; set; }
}

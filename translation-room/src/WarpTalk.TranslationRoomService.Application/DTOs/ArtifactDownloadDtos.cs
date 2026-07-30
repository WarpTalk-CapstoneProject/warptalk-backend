namespace WarpTalk.TranslationRoomService.Application.DTOs;

public sealed record ArtifactDownloadDto(
    string? Url,
    string? Content,
    string FileName,
    string ContentType);

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.WorkspaceService.Application.Interfaces;

public interface ITranslationRoomClient
{
    Task<TranslationRoomDto?> GetTranslationRoomAsync(Guid roomId, CancellationToken ct = default);
    Task<List<TranslationRoomParticipantDto>> GetParticipantsAsync(Guid roomId, CancellationToken ct = default);
}

public record TranslationRoomDto
{
    public Guid Id { get; init; }
    public Guid WorkspaceId { get; init; }
    public string Title { get; init; } = string.Empty;
    public Guid HostId { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime? StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
}

public record TranslationRoomParticipantDto
{
    public Guid Id { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public bool IsActive { get; init; }
}

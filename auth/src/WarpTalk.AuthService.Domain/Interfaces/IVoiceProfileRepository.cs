using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

public interface IVoiceProfileRepository : IGenericRepository<VoiceProfile>
{
    Task<IReadOnlyList<VoiceProfile>> GetByUserIdAsync(Guid userId, Guid? workspaceId = null, CancellationToken ct = default);
    Task<VoiceProfile?> GetByIdForUserAsync(Guid id, Guid userId, CancellationToken ct = default);
}

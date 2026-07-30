using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

public interface IVoiceProfileRepository : IGenericRepository<VoiceProfile>
{
    Task<IReadOnlyList<VoiceProfile>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task<VoiceProfile?> GetByIdForUserAsync(Guid id, Guid userId, CancellationToken ct = default);
    void Add(VoiceProfile entity);
}

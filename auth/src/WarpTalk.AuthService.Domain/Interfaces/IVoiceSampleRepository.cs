using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

public interface IVoiceSampleRepository : IGenericRepository<VoiceSample>
{
    Task<IReadOnlyList<VoiceSample>> GetByVoiceProfileIdAsync(Guid voiceProfileId, CancellationToken ct = default);
}

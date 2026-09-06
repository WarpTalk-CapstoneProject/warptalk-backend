using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Domain.Interfaces;

public interface IPluginConfirmationTokenRepository : IGenericRepository<PluginConfirmationToken>
{
    Task<bool> TryConsumeAsync(Guid tokenId, DateTime utcNow, CancellationToken ct = default);
}

using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IPluginInstallationService
{
    Task<Result<IReadOnlyList<PluginCatalogItemDto>>> ListCatalogAsync(Guid userId, CancellationToken ct = default);
    Task<Result<PluginCatalogItemDto>> InstallAsync(string pluginKey, Guid userId, CancellationToken ct = default);
    Task<Result> DisableAsync(string pluginKey, Guid userId, CancellationToken ct = default);
}

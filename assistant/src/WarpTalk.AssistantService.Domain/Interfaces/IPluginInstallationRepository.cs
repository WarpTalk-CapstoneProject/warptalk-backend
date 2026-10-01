using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Domain.Interfaces;

public interface IPluginInstallationRepository : IGenericRepository<PluginInstallation>
{
    /// <summary>
    /// How many users have each catalog row installed now, keyed by plugin id. A removed
    /// (<c>disabled</c>) installation is not counted.
    /// </summary>
    /// <remarks>
    /// One grouped COUNT rather than loading <c>plugin_installations</c> and counting in memory:
    /// this backs the admin listing, which asks about every row at once, and the table has a row
    /// per user per plugin.
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, int>> CountByPluginAsync(CancellationToken ct = default);

    /// <summary>How many installations reference one catalog row.</summary>
    Task<int> CountForPluginAsync(Guid pluginId, CancellationToken ct = default);
}

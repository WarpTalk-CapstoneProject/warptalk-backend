using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Domain.Interfaces;

public interface IPluginConnectionRepository : IGenericRepository<PluginConnection>
{
    /// <summary>
    /// How many connections still name one catalog row in <c>plugin_id</c>.
    /// </summary>
    /// <remarks>
    /// This is the count that decides whether a hard delete is possible, so it deliberately counts
    /// on <c>plugin_id</c> and not on <c>provider</c>. A connection's identity has been
    /// <c>(user_id, provider)</c> since 20260907101000 and <c>plugin_id</c> is only provenance -
    /// but <c>plugin_connections_plugin_id_fkey</c> is still declared on <c>plugin_id</c> and is
    /// <c>ON DELETE RESTRICT</c>, so <c>plugin_id</c> is exactly what the database will refuse the
    /// delete over.
    /// </remarks>
    Task<int> CountForPluginAsync(Guid pluginId, CancellationToken ct = default);
}

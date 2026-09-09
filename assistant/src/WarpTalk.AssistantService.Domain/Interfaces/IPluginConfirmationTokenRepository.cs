using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Domain.Interfaces;

public interface IPluginConfirmationTokenRepository : IGenericRepository<PluginConfirmationToken>
{
    Task<bool> TryConsumeAsync(Guid tokenId, DateTime utcNow, CancellationToken ct = default);

    /// <summary>
    /// How many confirmation tokens a catalog row would take with it if it were deleted. WT-646.
    /// </summary>
    /// <remarks>
    /// <c>plugin_confirmation_tokens_plugin_id_fkey</c> is <c>ON DELETE CASCADE</c> like the audit
    /// table's. Unlike audits these rows are genuinely disposable - a token lives five minutes and
    /// is only useful to the one user who is mid-call on the plugin being deleted - so the count is
    /// reported rather than used to refuse. It is reported because "what did this delete take" is a
    /// question the answer should not leave the operator to guess at.
    /// </remarks>
    Task<int> CountForPluginAsync(Guid pluginId, CancellationToken ct = default);
}

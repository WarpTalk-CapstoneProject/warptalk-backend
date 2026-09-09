using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// The operator-side lifecycle of a catalog row: read it, edit it, re-credential it, re-tool it,
/// retire it.
/// </summary>
/// <remarks>
/// Separate from <see cref="IPluginInstallationService"/> on purpose. That service answers "what
/// can this user install and connect", scoped to one caller's own rows; this one writes the global
/// catalog every user reads. Folding them together would put an unauthenticated-user concern and a
/// system-admin concern behind one type, where the only thing keeping them apart is remembering to
/// attach the right attribute to each method.
/// <para>
/// Every method takes the acting admin's id and records it in <c>plugins.updated_by</c>, so "who
/// changed this row" is answerable from the row itself rather than from a log that may have rolled.
/// </para>
/// </remarks>
public interface IPluginCatalogAdminService
{
    /// <summary>
    /// Every catalog row, active and retired alike, newest curation order first.
    /// </summary>
    Task<Result<IReadOnlyList<PluginCatalogAdminListItemDto>>> ListAsync(CancellationToken ct = default);

    /// <summary>One row in full, including its tool manifest.</summary>
    Task<Result<PluginCatalogAdminDetailDto>> GetAsync(string pluginKey, CancellationToken ct = default);

    /// <summary>Applies a partial edit; only the supplied properties change.</summary>
    Task<Result<PluginCatalogAdminDetailDto>> UpdateAsync(
        string pluginKey,
        UpdatePluginCatalogRequest request,
        Guid adminUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Sets or rotates the pre-registered OAuth client and moves the row to
    /// <c>oauth_client_source = 'preregistered'</c>.
    /// </summary>
    Task<Result<PluginCatalogAdminDetailDto>> SetOAuthClientAsync(
        string pluginKey,
        SetPluginOAuthClientRequest request,
        Guid adminUserId,
        CancellationToken ct = default);

    /// <summary>Replaces <c>tools_json</c> after validating the manifest against the tool contract.</summary>
    Task<Result<PluginCatalogAdminDetailDto>> ReplaceToolsAsync(
        string pluginKey,
        ReplacePluginToolsRequest request,
        Guid adminUserId,
        CancellationToken ct = default);

    /// <summary>Clears cached discovery output so the ladder runs again on the next connect.</summary>
    Task<Result<PluginCatalogAdminDetailDto>> RediscoverAsync(
        string pluginKey,
        Guid adminUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Retires a row. Soft by default (<c>is_active = false</c>); a hard delete is possible only
    /// while nothing references the row - installations, connections and tool audits alike.
    /// </summary>
    /// <remarks>
    /// Audits count as a reference even though their foreign key would not stop the delete: it
    /// cascades, so letting the delete through would take the plugin's whole recorded history with
    /// it, silently, in the case that occurs most often - a plugin everybody has already
    /// uninstalled. The returned <see cref="PluginCatalogDeleteResultDto"/> carries all four counts
    /// either way, so a refusal and a retirement both say what is still attached to the row.
    /// </remarks>
    Task<Result<PluginCatalogDeleteResultDto>> DeleteAsync(
        string pluginKey,
        bool hard,
        Guid adminUserId,
        CancellationToken ct = default);

    /// <summary>A page of this plugin's recorded tool invocations.</summary>
    Task<Result<PluginToolAuditPageDto>> ListAuditsAsync(
        string pluginKey,
        PluginToolAuditQueryDto query,
        CancellationToken ct = default);
}

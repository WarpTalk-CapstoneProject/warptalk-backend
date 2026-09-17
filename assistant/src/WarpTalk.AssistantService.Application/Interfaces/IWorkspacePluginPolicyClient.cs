namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IWorkspacePluginPolicyClient
{
    /// <summary>
    /// Whether this workspace permits its members to use plugins in it at all. WT-646.
    /// </summary>
    /// <remarks>
    /// Since the plugin marketplace this is the TRANSITION input only: it is read for a workspace
    /// whose Owner has never curated its plugin list (see WorkspacePluginAvailability), and ignored
    /// for one that has. True keeps every marketplace plugin available, as before; false, none.
    /// <para>
    /// FALSE when the workspace service cannot be reached or does not know the workspace. An
    /// unreadable policy has to read as "no", not as "no policy" - the alternative is that an
    /// outage in the workspace service silently lifts every workspace's plugin restriction.
    /// </para>
    /// </remarks>
    Task<bool> AllowsPluginUsageAsync(Guid workspaceId, CancellationToken ct = default);
}

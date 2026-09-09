namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IWorkspacePluginPolicyClient
{
    /// <summary>
    /// Whether this workspace permits its members to use plugins in it at all. WT-646.
    /// </summary>
    /// <remarks>
    /// One boolean, because that is the whole of a workspace's plugin policy: a workspace owner
    /// configures whether plugins may be used here, and nothing finer. Named for the question
    /// rather than for a policy object, so a caller cannot read the wrong field off a wider
    /// result.
    /// <para>
    /// FALSE when the workspace service cannot be reached or does not know the workspace. An
    /// unreadable policy has to read as "no", not as "no policy" - the alternative is that an
    /// outage in the workspace service silently lifts every workspace's plugin restriction.
    /// </para>
    /// </remarks>
    Task<bool> AllowsPluginUsageAsync(Guid workspaceId, CancellationToken ct = default);
}

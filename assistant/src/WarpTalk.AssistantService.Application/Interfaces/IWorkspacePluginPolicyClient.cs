namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// What the workspace service said about a workspace's AllowAnyPlugins switch - including that it
/// said nothing.
/// </summary>
/// <remarks>
/// Three answers, not two, because the two callers need different things from an outage. The
/// guard, deciding whether a plugin may run right now, needs "no" and gets it from
/// <see cref="IWorkspacePluginPolicyClient.AllowsPluginUsageAsync"/>. The first edit of a
/// workspace's list WRITES the answer down for good - it seeds the list from it - and an outage
/// read as "no" there would record every member as having lost every plugin. That caller has to be
/// able to tell "the switch is off" from "nobody answered", and refuse on the second.
/// </remarks>
public enum WorkspacePluginPolicyAnswer
{
    /// <summary>AllowAnyPlugins is on: every marketplace plugin, while the list is uncurated.</summary>
    Allowed,

    /// <summary>AllowAnyPlugins is off: none, while the list is uncurated.</summary>
    NotAllowed,

    /// <summary>
    /// The workspace service could not be reached, or did not know a workspace that must exist.
    /// Not an answer, and never to be persisted as one.
    /// </summary>
    Unknown,
}

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
    /// <para>
    /// That fail-closed reading is right for a decision made now and forgotten. It is wrong for one
    /// that is written down: use <see cref="ReadAllowAnyPluginsAsync"/> there.
    /// </para>
    /// </remarks>
    Task<bool> AllowsPluginUsageAsync(Guid workspaceId, CancellationToken ct = default);

    /// <summary>
    /// The same switch, with an outage reported as <see cref="WorkspacePluginPolicyAnswer.Unknown"/>
    /// rather than folded into "no". Never throws.
    /// </summary>
    /// <remarks>
    /// For the transition's first write, which seeds a workspace's list from this answer and never
    /// asks again. See <see cref="WorkspacePluginPolicyAnswer"/>.
    /// </remarks>
    Task<WorkspacePluginPolicyAnswer> ReadAllowAnyPluginsAsync(Guid workspaceId, CancellationToken ct = default);
}

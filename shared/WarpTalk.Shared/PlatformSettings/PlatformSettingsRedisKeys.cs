namespace WarpTalk.Shared.PlatformSettings;

/// <summary>
/// Where the published settings live in Redis. The workspace service is the only writer; every
/// other service (and the Python workers in warptalk-ai, which mirror these names in
/// <c>app/shared/platform_settings.py</c>) only reads.
///
/// <list type="bullet">
/// <item><c>platform:settings:v1:platform</c> — hash, field = setting key, value = JSON. Always
/// carries <see cref="VersionField"/>, so an existing-but-empty snapshot ("nothing set") is
/// distinguishable from a missing one (evicted, or Redis restarted).</item>
/// <item><c>platform:settings:v1:plan:{slug}</c> and <c>platform:settings:v1:workspace:{id}</c> —
/// hashes of overrides for that plan or workspace.</item>
/// <item><c>platform:settings:v1:changed</c> — pub/sub channel announcing a new version. Nobody has
/// to listen: readers re-read on a short TTL, and the channel is there for tools and dashboards.</item>
/// </list>
///
/// Redis here runs allkeys-lru, so any of these can be evicted. The writer re-publishes the whole
/// snapshot every minute (<c>PlatformSettingsPublisherWorker</c>), and a reader that finds the
/// platform hash missing keeps the last snapshot it read rather than falling back to defaults.
/// </summary>
public static class PlatformSettingsRedisKeys
{
    public const string Prefix = "platform:settings:v1:";
    public const string PlatformHash = Prefix + "platform";
    public const string ChangedChannel = Prefix + "changed";
    public const string VersionField = "__version";

    public static string PlanHash(string planSlug) => Prefix + "plan:" + planSlug.Trim().ToLowerInvariant();

    public static string WorkspaceHash(Guid workspaceId) => Prefix + "workspace:" + workspaceId.ToString("D");
}

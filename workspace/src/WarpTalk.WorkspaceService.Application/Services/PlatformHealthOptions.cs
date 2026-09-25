namespace WarpTalk.WorkspaceService.Application.Services;

/// <summary>
/// Deployment facts the System Health screen needs and cannot discover.
/// </summary>
public sealed class PlatformHealthOptions
{
    /// <summary>
    /// <c>Monitoring:GrafanaEmbedPath</c> — the same-origin path Grafana is published under behind
    /// the admin-only ForwardAuth (production: <c>/grafana</c>). Unset everywhere Grafana is not
    /// published, and the screen then leaves the embedded charts out rather than framing a 404.
    /// </summary>
    public string? GrafanaEmbedPath { get; init; }
}

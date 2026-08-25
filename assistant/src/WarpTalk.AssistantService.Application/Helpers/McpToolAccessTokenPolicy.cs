using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Helpers;

internal static class McpToolAccessTokenPolicy
{
    internal static bool IsExpiredOrExpiring(PluginConnection connection)
    {
        return connection.AccessTokenExpiresAt.HasValue
            && connection.AccessTokenExpiresAt.Value - TimeSpan.FromSeconds(60) <= DateTime.UtcNow;
    }
}

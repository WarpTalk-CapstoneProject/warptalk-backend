using WarpTalk.AssistantService.Application.DTOs;

namespace WarpTalk.AssistantService.Application.Mappers;

public static class PluginOAuthRefreshResultMapper
{
    public static bool IsSuccess(PluginOAuthRefreshResultDto result) =>
        result.Outcome == PluginOAuthRefreshOutcome.Succeeded;

    public static PluginOAuthRefreshResultDto Succeeded(PluginOAuthTokenDto token) =>
        new(PluginOAuthRefreshOutcome.Succeeded, token);

    public static PluginOAuthRefreshResultDto GrantRejected(string detail) =>
        new(PluginOAuthRefreshOutcome.GrantRejected, null, detail);

    public static PluginOAuthRefreshResultDto ProviderUnavailable(string detail) =>
        new(PluginOAuthRefreshOutcome.ProviderUnavailable, null, detail);

    public static PluginOAuthRefreshResultDto ProviderRateLimited(string detail) =>
        new(PluginOAuthRefreshOutcome.ProviderRateLimited, null, detail);
}

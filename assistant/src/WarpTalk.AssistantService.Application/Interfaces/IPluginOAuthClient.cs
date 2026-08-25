using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IPluginOAuthClient
{
    string BuildAuthorizationUrl(Plugin plugin, IReadOnlyList<string> scopes, string state);

    Task<PluginOAuthTokenDto> ExchangeCodeAsync(
        Plugin plugin,
        string code,
        CancellationToken ct = default);
}

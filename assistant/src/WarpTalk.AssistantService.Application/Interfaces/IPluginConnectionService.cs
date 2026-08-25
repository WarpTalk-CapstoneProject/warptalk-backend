using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IPluginConnectionService
{
    Task<Result<PluginConnectUrlDto>> GetConnectUrlAsync(string pluginKey, Guid userId, CancellationToken ct = default);
    Task<Result<PluginConnectionStatusDto>> CompleteOAuthCallbackAsync(string pluginKey, string code, string state, CancellationToken ct = default);
    Task<Result<PluginConnectionStatusDto>> GetStatusAsync(string pluginKey, Guid userId, CancellationToken ct = default);
    Task<Result> DisconnectAsync(string pluginKey, Guid userId, CancellationToken ct = default);
}

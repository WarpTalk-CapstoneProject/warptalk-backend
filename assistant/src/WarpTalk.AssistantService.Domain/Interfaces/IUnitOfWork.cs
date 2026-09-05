using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IAssistantConversationRepository AssistantConversationRepository { get; }
    IAssistantMessageRepository AssistantMessageRepository { get; }
    IAssistantToolCallRepository AssistantToolCallRepository { get; }
    IPluginRepository PluginRepository { get; }
    IPluginInstallationRepository PluginInstallationRepository { get; }
    IPluginConnectionRepository PluginConnectionRepository { get; }
    IPluginToolAuditRepository PluginToolAuditRepository { get; }
    IPluginConfirmationTokenRepository PluginConfirmationTokenRepository { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

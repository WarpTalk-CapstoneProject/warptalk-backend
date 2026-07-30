using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IAssistantConversationRepository AssistantConversationRepository { get; }
    IAssistantMessageRepository AssistantMessageRepository { get; }
    IAssistantToolCallRepository AssistantToolCallRepository { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

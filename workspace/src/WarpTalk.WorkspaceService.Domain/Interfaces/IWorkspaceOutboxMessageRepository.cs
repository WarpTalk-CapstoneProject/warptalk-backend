using WarpTalk.WorkspaceService.Domain.Entities;

namespace WarpTalk.WorkspaceService.Domain.Interfaces;

/// <summary>
/// Outbox rows written in the same transaction as the change they describe. Reached through the
/// unit of work rather than the removed generic Repository&lt;T&gt;() factory, so the outbox has a
/// named seam like every other table.
/// </summary>
public interface IWorkspaceOutboxMessageRepository : IGenericRepository<WorkspaceOutboxMessage>
{
}

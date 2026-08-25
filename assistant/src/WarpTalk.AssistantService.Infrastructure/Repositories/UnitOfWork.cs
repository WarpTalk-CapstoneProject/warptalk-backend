using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Persistence;

namespace WarpTalk.AssistantService.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly AssistantDbContext _db;

    public UnitOfWork(AssistantDbContext db)
    {
        _db = db;
        AssistantConversationRepository = new AssistantConversationRepository(db);
        AssistantMessageRepository = new AssistantMessageRepository(db);
        AssistantToolCallRepository = new AssistantToolCallRepository(db);
        PluginRepository = new PluginRepository(db);
        PluginInstallationRepository = new PluginInstallationRepository(db);
        PluginConnectionRepository = new PluginConnectionRepository(db);
        PluginToolAuditRepository = new PluginToolAuditRepository(db);
    }

    public IAssistantConversationRepository AssistantConversationRepository { get; }
    public IAssistantMessageRepository AssistantMessageRepository { get; }
    public IAssistantToolCallRepository AssistantToolCallRepository { get; }
    public IPluginRepository PluginRepository { get; }
    public IPluginInstallationRepository PluginInstallationRepository { get; }
    public IPluginConnectionRepository PluginConnectionRepository { get; }
    public IPluginToolAuditRepository PluginToolAuditRepository { get; }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);

    public void Dispose() => _db.Dispose();
}

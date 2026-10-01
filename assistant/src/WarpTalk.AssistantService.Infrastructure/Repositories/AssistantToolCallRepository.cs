using Microsoft.EntityFrameworkCore;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Persistence;

namespace WarpTalk.AssistantService.Infrastructure.Repositories;

public class AssistantToolCallRepository : GenericRepository<AssistantToolCall>, IAssistantToolCallRepository
{
    private readonly AssistantDbContext _db;

    public AssistantToolCallRepository(AssistantDbContext context) : base(context)
    {
        _db = context;
    }

    public async Task<IReadOnlyList<AssistantToolCallInsightRow>> ListForInsightsAsync(
        Guid? workspaceId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        // Projected, untracked: the insights never need the entity, and arguments_json /
        // result_json are not read at all.
        return await Window(workspaceId, fromUtc, toUtc)
            .AsNoTracking()
            .Select(call => new AssistantToolCallInsightRow(
                call.WorkspaceId,
                call.ToolName,
                call.Source,
                call.PluginKey,
                call.Outcome,
                call.DurationMs,
                call.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<(int Calls, int Ok)> CountForPeriodAsync(
        Guid? workspaceId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var query = Window(workspaceId, fromUtc, toUtc);
        var calls = await query.CountAsync(ct);
        var ok = calls == 0
            ? 0
            : await query.CountAsync(call => call.Outcome == AssistantToolCallConstants.Outcomes.Ok, ct);
        return (calls, ok);
    }

    public async Task<DateTime?> GetEarliestCreatedAtAsync(CancellationToken ct = default) =>
        await _db.AssistantToolCalls.MinAsync(call => (DateTime?)call.CreatedAt, ct);

    private IQueryable<AssistantToolCall> Window(Guid? workspaceId, DateTime fromUtc, DateTime toUtc)
    {
        var query = _db.AssistantToolCalls.Where(call => call.CreatedAt >= fromUtc && call.CreatedAt < toUtc);
        if (workspaceId is { } id)
            query = query.Where(call => call.WorkspaceId == id);
        return query;
    }
}

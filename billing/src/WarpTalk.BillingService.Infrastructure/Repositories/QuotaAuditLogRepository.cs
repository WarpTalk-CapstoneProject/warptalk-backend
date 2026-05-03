using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class QuotaAuditLogRepository : IQuotaAuditLogRepository
{
    private readonly BillingDbContext _db;

    public QuotaAuditLogRepository(BillingDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(
        QuotaAuditLog entity,
        CancellationToken ct = default)
    {
        await _db.QuotaAuditLogs.AddAsync(entity, ct);
    }

    public async Task AddRangeAsync(
        IEnumerable<QuotaAuditLog> logs,
        CancellationToken ct = default)
    {
        await _db.QuotaAuditLogs.AddRangeAsync(logs, ct);
    }

    public async Task<IReadOnlyList<QuotaAuditLog>> GetByUserIdAsync(
        Guid userId,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        var safeSkip = Math.Max(0, skip);
        var safeTake = Math.Clamp(take, 1, 200);

        return await _db.QuotaAuditLogs
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .Skip(safeSkip)
            .Take(safeTake)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<QuotaAuditLog>> GetByMeetingIdAsync(
        Guid meetingId,
        CancellationToken ct = default)
    {
        return await _db.QuotaAuditLogs
            .AsNoTracking()
            .Where(x => x.MeetingId == meetingId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
    }
}
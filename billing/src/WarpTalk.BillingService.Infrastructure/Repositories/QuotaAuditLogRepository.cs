using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class QuotaAuditLogRepository : IQuotaAuditLogRepository
{
    private readonly BillingDbContext _dbContext;

    public QuotaAuditLogRepository(BillingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IEnumerable<QuotaAuditLog>> GetByWorkspaceIdAsync(Guid workspaceId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 200);

        return await _dbContext.QuotaAuditLogs
            .Where(l => l.WorkspaceId == workspaceId)
            .OrderByDescending(l => l.CreatedAt)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(QuotaAuditLog log, CancellationToken cancellationToken = default)
    {
        await _dbContext.QuotaAuditLogs.AddAsync(log, cancellationToken);
    }
}

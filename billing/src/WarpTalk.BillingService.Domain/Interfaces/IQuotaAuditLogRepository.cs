using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IQuotaAuditLogRepository
{
    Task<IEnumerable<QuotaAuditLog>> GetByWorkspaceIdAsync(Guid workspaceId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default);
    Task AddAsync(QuotaAuditLog log, CancellationToken cancellationToken = default);
}

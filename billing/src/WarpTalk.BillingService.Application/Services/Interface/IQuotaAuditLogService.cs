using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Services.Interface
{
    public interface IQuotaAuditLogService
    {
        Task LogAsync(AuditLogCommand command, CancellationToken ct = default);

        Task<IReadOnlyList<QuotaAuditLog>> GetByUserAsync(
            Guid userId,
            int skip,
            int take,
            CancellationToken ct = default);

        Task<IReadOnlyList<QuotaAuditLog>> GetByWorkspaceAsync(
            Guid workspaceId,
            int page,
            int pageSize,
            CancellationToken ct = default);
    }
}

// =======================================================
// IQuotaAuditLogRepository.cs
// =======================================================

using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

/// <summary>
/// Repository for quota audit log operations
/// </summary>
public interface IQuotaAuditLogRepository
{
    Task<IReadOnlyList<QuotaAuditLog>> GetByUserIdAsync(
        Guid userId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QuotaAuditLog>> GetByMeetingIdAsync(
        Guid meetingId,
        CancellationToken cancellationToken = default);
    Task AddAsync(
        QuotaAuditLog log,
        CancellationToken cancellationToken = default);

    Task AddRangeAsync(
        IEnumerable<QuotaAuditLog> logs,
        CancellationToken cancellationToken = default);
}
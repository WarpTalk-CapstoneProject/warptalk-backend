// =======================================================
// IMeetingUsageSessionRepository.cs
// =======================================================

using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IMeetingUsageSessionRepository
{
    Task AddAsync(MeetingUsageSession entity, CancellationToken ct = default);

    Task<MeetingUsageSession?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<MeetingUsageSession?> GetActiveByMeetingIdAsync(Guid meetingId, CancellationToken ct = default);

    Task UpdateAsync(MeetingUsageSession entity, CancellationToken ct = default);
}
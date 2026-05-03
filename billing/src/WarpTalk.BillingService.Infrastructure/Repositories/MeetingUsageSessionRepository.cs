// =======================================================
// MeetingUsageSessionRepository.cs
// =======================================================

using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class MeetingUsageSessionRepository
    : IMeetingUsageSessionRepository
{
    private readonly BillingDbContext _dbContext;

    public MeetingUsageSessionRepository(
        BillingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<MeetingUsageSession?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.MeetingUsageSessions
            .FirstOrDefaultAsync(
                x => x.Id == id,
                cancellationToken);
    }

    // ✅ FIX: implement đúng interface yêu cầu
    public async Task<MeetingUsageSession?> GetByMeetingIdAsync(
        Guid meetingId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.MeetingUsageSessions
            .FirstOrDefaultAsync(
                x => x.MeetingId == meetingId,
                cancellationToken);
    }

    // optional: giữ helper method nếu bạn cần
    public async Task<MeetingUsageSession?> GetActiveByMeetingIdAsync(
        Guid meetingId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.MeetingUsageSessions
            .FirstOrDefaultAsync(
                x =>
                    x.MeetingId == meetingId &&
                    x.Status == UsageSessionStatus.Active,
                cancellationToken);
    }

    public async Task AddAsync(
        MeetingUsageSession session,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.MeetingUsageSessions
            .AddAsync(session, cancellationToken);
    }

    public Task UpdateAsync(
        MeetingUsageSession session,
        CancellationToken cancellationToken = default)
    {
        _dbContext.MeetingUsageSessions.Update(session);
        return Task.CompletedTask;
    }
}
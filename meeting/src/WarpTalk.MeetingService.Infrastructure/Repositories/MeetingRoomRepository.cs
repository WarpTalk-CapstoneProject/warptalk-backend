using Microsoft.EntityFrameworkCore;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class MeetingRoomRepository : GenericRepository<MeetingRoom>, IMeetingRoomRepository
{
    public MeetingRoomRepository(MeetingDbContext context) : base(context)
    {
    }

    public Task<int> ClearActiveHostIfAsync(Guid translationRoomId, Guid expectedHostId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return _dbSet
            .Where(r => r.TranslationRoomId == translationRoomId && r.ActiveHostId == expectedHostId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.ActiveHostId, (Guid?)null)
                    .SetProperty(r => r.UpdatedAt, now),
                ct);
    }

    public async Task<bool> TryMarkFinishedAsync(Guid meetingRoomId, DateTime endedAtUtc, CancellationToken ct = default)
    {
        var claimed = await _dbSet
            .Where(r => r.Id == meetingRoomId && r.EndedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.Status, FinishedStatus)
                    .SetProperty(r => r.EndedAt, (DateTime?)endedAtUtc)
                    .SetProperty(r => r.UpdatedAt, endedAtUtc),
                ct);
        if (claimed > 0)
        {
            return true;
        }

        await _dbSet
            .Where(r => r.Id == meetingRoomId && r.Status != FinishedStatus)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.Status, FinishedStatus)
                    .SetProperty(r => r.UpdatedAt, endedAtUtc),
                ct);
        return false;
    }

    private const string FinishedStatus = "FINISHED";
}

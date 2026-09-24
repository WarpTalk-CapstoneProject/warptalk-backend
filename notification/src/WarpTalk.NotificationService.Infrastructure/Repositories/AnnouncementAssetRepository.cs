using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class AnnouncementAssetRepository : IAnnouncementAssetRepository
{
    private readonly NotificationDbContext _context;

    public AnnouncementAssetRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(AnnouncementAsset asset, CancellationToken ct = default) =>
        await _context.AnnouncementAssets.AddAsync(asset, ct);

    public Task<AnnouncementAsset?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _context.AnnouncementAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
}

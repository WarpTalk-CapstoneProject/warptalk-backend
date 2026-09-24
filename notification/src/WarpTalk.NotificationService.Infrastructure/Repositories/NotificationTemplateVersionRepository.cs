using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class NotificationTemplateVersionRepository : INotificationTemplateVersionRepository
{
    private readonly NotificationDbContext _context;

    public NotificationTemplateVersionRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(NotificationTemplateVersion version, CancellationToken ct = default) =>
        await _context.NotificationTemplateVersions.AddAsync(version, ct);

    public async Task<IReadOnlyList<NotificationTemplateVersion>> ListAsync(string type, string channel, int take, CancellationToken ct = default) =>
        await _context.NotificationTemplateVersions
            .AsNoTracking()
            .Where(v => v.TemplateType == type && v.Channel == channel)
            .OrderByDescending(v => v.Version)
            .Take(take)
            .ToListAsync(ct);

    public Task<NotificationTemplateVersion?> GetAsync(string type, string channel, int version, CancellationToken ct = default) =>
        _context.NotificationTemplateVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.TemplateType == type && v.Channel == channel && v.Version == version, ct);
}

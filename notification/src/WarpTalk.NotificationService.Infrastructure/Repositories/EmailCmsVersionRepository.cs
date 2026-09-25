using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class EmailCmsVersionRepository : IEmailCmsVersionRepository
{
    private readonly NotificationDbContext _context;

    public EmailCmsVersionRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(EmailCmsVersion version, CancellationToken ct = default) =>
        await _context.EmailCmsVersions.AddAsync(version, ct);

    public async Task<IReadOnlyList<EmailCmsVersion>> ListAsync(string ownerType, Guid ownerId, int take, CancellationToken ct = default) =>
        await _context.EmailCmsVersions
            .AsNoTracking()
            .Where(v => v.OwnerType == ownerType && v.OwnerId == ownerId)
            .OrderByDescending(v => v.Version)
            .Take(take)
            .ToListAsync(ct);

    public Task<EmailCmsVersion?> GetAsync(string ownerType, Guid ownerId, int version, CancellationToken ct = default) =>
        _context.EmailCmsVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.OwnerType == ownerType && v.OwnerId == ownerId && v.Version == version, ct);
}

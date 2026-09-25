using Microsoft.EntityFrameworkCore;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class StaffInvitationRepository : GenericRepository<StaffInvitation>, IStaffInvitationRepository
{
    public StaffInvitationRepository(AuthDbContext context) : base(context)
    {
    }

    public Task<StaffInvitation?> GetPendingByEmailAsync(string normalizedEmail, DateTime now, CancellationToken ct = default) =>
        _dbSet
            .Include(i => i.Role)
            .Where(i => i.Email == normalizedEmail
                && i.AcceptedAt == null
                && i.RevokedAt == null
                && i.ExpiresAt > now)
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public Task<StaffInvitation?> GetOpenByEmailAsync(string normalizedEmail, CancellationToken ct = default) =>
        _dbSet
            .Include(i => i.Role)
            .FirstOrDefaultAsync(i => i.Email == normalizedEmail && i.AcceptedAt == null && i.RevokedAt == null, ct);

    public async Task<IReadOnlyList<StaffInvitation>> ListAsync(CancellationToken ct = default) =>
        await _dbSet.Include(i => i.Role).OrderByDescending(i => i.CreatedAt).ToListAsync(ct);

    public Task<int> CountPendingWithRoleAsync(Guid roleId, DateTime now, CancellationToken ct = default) =>
        _dbSet.CountAsync(i => i.RoleId == roleId && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now, ct);
}

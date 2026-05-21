using Microsoft.EntityFrameworkCore;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class UserRepository : GenericRepository<User>, IUserRepository
{
    public UserRepository(AuthDbContext db) : base(db)
    {
    }

    public async Task<bool> ExistsByEmailAsync(string email, CancellationToken ct = default)
    {
        return await _set.AnyAsync(u => u.Email == email, ct);
    }

    public async Task<User?> GetByEmailWithRolesAsync(string email, CancellationToken ct = default)
    {
        return await _set.Include(u => u.UserRoleUsers)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Email == email, ct);
    }

    public async Task<User?> GetByIdWithRolesAsync(Guid id, CancellationToken ct = default)
    {
        return await _set.Include(u => u.UserRoleUsers)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == id, ct);
    }

    public async Task<User?> GetByGoogleIdWithRolesAsync(string googleId, CancellationToken ct = default)
    {
        return await _set.Include(u => u.UserRoleUsers)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.GoogleId == googleId, ct);
    }
}

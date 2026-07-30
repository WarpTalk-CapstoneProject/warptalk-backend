using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class UserRepository : GenericRepository<User>, IUserRepository
{
    public UserRepository(AuthDbContext context) : base(context)
    {
    }

    public async Task<bool> ExistsByEmailAsync(string email, CancellationToken ct = default)
    {
        return await _dbSet.AnyAsync(u => u.Email == email, ct);
    }

    public async Task<User?> GetByEmailWithRolesAsync(string email, CancellationToken ct = default)
    {
        return await _dbSet.Include(u => u.UserRoleUsers)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Email == email, ct);
    }

    public async Task<User?> GetByIdWithRolesAsync(Guid id, CancellationToken ct = default)
    {
        return await _dbSet.Include(u => u.UserRoleUsers)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == id, ct);
    }

    public async Task<User?> GetByGoogleIdWithRolesAsync(string googleId, CancellationToken ct = default)
    {
        return await _dbSet.Include(u => u.UserRoleUsers)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.GoogleId == googleId, ct);
    }

    public Task<User?> GetByEmailVerificationTokenHashAsync(string tokenHash, CancellationToken ct = default)
        => _dbSet.FirstOrDefaultAsync(u => u.EmailVerificationTokenHash == tokenHash, ct);

    public Task<User?> GetByPasswordResetTokenHashAsync(string tokenHash, CancellationToken ct = default)
        => _dbSet.FirstOrDefaultAsync(u => u.PasswordResetTokenHash == tokenHash, ct);
}

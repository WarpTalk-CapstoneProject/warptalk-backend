using Microsoft.EntityFrameworkCore;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class PermissionRepository : GenericRepository<Permission>, IPermissionRepository
{
    public PermissionRepository(AuthDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<Permission>> GetByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken ct = default) =>
        await _dbSet.Where(p => codes.Contains(p.Code) && p.IsActive && p.DeletedAt == null).ToListAsync(ct);
}

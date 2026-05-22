using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class UserSettingRepository : GenericRepository<UserSetting>, IUserSettingRepository
{
    public UserSettingRepository(AuthDbContext db) : base(db)
    {
    }

    public async Task<UserSetting?> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        return await FirstOrDefaultAsync(s => s.UserId == userId, ct: ct);
    }

    public void Add(UserSetting entity)
    {
        _set.Add(entity);
    }
}


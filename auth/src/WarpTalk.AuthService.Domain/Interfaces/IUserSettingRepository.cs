using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

public interface IUserSettingRepository : IGenericRepository<UserSetting>
{
    Task<UserSetting?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);
    void Add(UserSetting entity);
}


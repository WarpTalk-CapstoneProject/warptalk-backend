using System;
using System.Threading.Tasks;

namespace WarpTalk.Shared.Interfaces;
//kiểm tra danh sách thu hồi/banned (Redis) để từ chối các user bị khóa
public interface ITokenBlacklistService
{
    Task<bool> IsUserBlacklistedAsync(Guid userId);
}

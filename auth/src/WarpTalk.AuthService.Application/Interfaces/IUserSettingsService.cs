using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Interfaces;

public interface IUserSettingsService
{
    Task<Result<UserSettingsDto>> GetSettingsAsync(Guid userId, CancellationToken ct = default);
    Task<Result<UserSettingsDto>> UpdateSettingsAsync(Guid userId, UpdateUserSettingsRequest request, CancellationToken ct = default);
}

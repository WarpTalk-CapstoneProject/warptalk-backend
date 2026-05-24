using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Controllers;

[ApiController]
[Route("api/v1/auth/settings")]
public class UserSettingsController : BaseApiController
{
    private readonly IUserSettingsService _userSettingsService;

    public UserSettingsController(IUserSettingsService userSettingsService)
    {
        _userSettingsService = userSettingsService;
    }

    [Authorize]
    [HttpGet]
    public async Task<Result<UserSettingsDto>> GetSettings(CancellationToken ct)
        => await _userSettingsService.GetSettingsAsync(CurrentUserId, ct);

    [Authorize]
    [HttpPut]
    public async Task<Result<UserSettingsDto>> UpdateSettings([FromBody] UpdateUserSettingsRequest request, CancellationToken ct)
        => await _userSettingsService.UpdateSettingsAsync(CurrentUserId, request, ct);
}


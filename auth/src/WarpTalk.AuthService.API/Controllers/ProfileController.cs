using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class ProfileController : BaseApiController
{
    private readonly IProfileService _profileService;

    public ProfileController(IProfileService profileService)
    {
        _profileService = profileService;
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<Result<UserDto>> GetProfile(CancellationToken ct)
        => await _profileService.GetProfileAsync(CurrentUserId, ct);

    [Authorize]
    [HttpPut("me")]
    public async Task<Result<UserDto>> UpdateProfile([FromBody] UpdateProfileRequest request, CancellationToken ct)
        => await _profileService.UpdateProfileAsync(CurrentUserId, request, ct);

    [Authorize]
    [HttpPost("change-password")]
    public async Task<Result> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
        => await _profileService.ChangePasswordAsync(CurrentUserId, request, ct);
}


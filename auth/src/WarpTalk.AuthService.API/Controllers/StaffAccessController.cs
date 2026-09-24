using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AuthService.API.Controllers;

/// <summary>
/// The caller's own staff access (G10). Not an admin endpoint: every signed-in user may ask, and
/// the answer for almost everyone is "not staff". The web renders the admin portal — sidebar,
/// pages, buttons, palette commands — from this, and the servers enforce the same answer anyway.
///
/// Served from the same cache the permission checks use, so what the page shows and what the
/// servers allow cannot disagree for longer than that cache's window.
/// </summary>
[ApiController]
[Route("api/v1/auth/staff-access")]
[Authorize]
public class StaffAccessController : ControllerBase
{
    private readonly IStaffAdminService _staff;

    public StaffAccessController(IStaffAdminService staff) => _staff = staff;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized(new ApiErrorResponse("The token carries no usable subject.", ErrorCodes.Unauthorized));

        Response.Headers.CacheControl = "no-store";
        return Ok(await _staff.GetSelfAccessAsync(userId.Value, ct));
    }
}

using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared.Authorization;
using WarpTalk.WorkspaceService.Application.Interfaces;

namespace WarpTalk.WorkspaceService.API.Controllers;

/// <summary>
/// The platform's own vital signs, read back out of the metrics store.
///
/// Read-only and query-only: nothing here can silence an alert, restart a container or write a
/// sample. When the store cannot be reached the response still returns 200 carrying
/// <c>monitoringAvailable: false</c> — a 500 would be indistinguishable, to the screen, from the
/// platform being down.
/// </summary>
[ApiController]
[Route("api/v1/admin/platform-health")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public class AdminPlatformHealthController : ControllerBase
{
    private readonly IAdminPlatformHealthService _health;

    public AdminPlatformHealthController(IAdminPlatformHealthService health)
    {
        _health = health;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
        => Ok(await _health.ReadAsync(ct));
}

using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.NotificationService.Application.Services;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.NotificationService.API.Controllers;

/// <summary>
/// The pending-work inbox source for content (G12): scheduled announcements to review, stale drafts and
/// failed broadcasts. Read by the workspace service's inbox with the staff member's own token; under
/// the admin-notification route the gateway already forwards here.
/// </summary>
[ApiController]
[Route("api/v1/admin/notifications/inbox-items")]
public sealed class AdminContentInboxController : ControllerBase
{
    private readonly IContentInboxSourceService _source;

    public AdminContentInboxController(IContentInboxSourceService source)
    {
        _source = source;
    }

    [HttpGet]
    [RequirePermission(AdminPermissions.ContentAnnouncements)]
    public async Task<ActionResult<AdminInboxSourceResponse>> GetItems(CancellationToken ct)
        => Ok(await _source.GetItemsAsync(ct));
}

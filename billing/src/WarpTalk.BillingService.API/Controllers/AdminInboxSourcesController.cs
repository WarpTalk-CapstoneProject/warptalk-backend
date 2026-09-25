using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.API.Controllers;

/// <summary>
/// Billing's sources for the pending-work inbox (G12). The workspace service's inbox calls these with
/// the staff member's own token, so each answers only to the permission that already lets them see
/// that data: a 403 here is how the inbox knows to leave the source out for that person.
///
/// Both paths sit under prefixes the gateway already forwards to billing (admin-billing-route,
/// admin-providers-route). The operating-expense source lives on AdminExpensesController.
/// </summary>
[ApiController]
public sealed class AdminInboxSourcesController : ControllerBase
{
    private readonly IAdminInboxSourceService _sources;

    public AdminInboxSourcesController(IAdminInboxSourceService sources)
    {
        _sources = sources;
    }

    /// <summary>New sales leads, past-due and awaiting-payment invoices, disputes, trials and renewals ending, suspended service.</summary>
    [HttpGet("~/api/v1/admin/billing/inbox-items")]
    [RequirePermission(AdminPermissions.BillingRead)]
    public async Task<ActionResult<AdminInboxSourceResponse>> GetBillingItems(CancellationToken ct)
        => Ok(await _sources.GetBillingItemsAsync(ct));

    /// <summary>Open provider incidents and quota (402) refusals in the last 24 hours.</summary>
    [HttpGet("~/api/v1/admin/providers/inbox-items")]
    [RequirePermission(AdminPermissions.ProvidersRead)]
    public async Task<ActionResult<AdminInboxSourceResponse>> GetProviderItems(CancellationToken ct)
        => Ok(await _sources.GetProviderItemsAsync(ct));
}

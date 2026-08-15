using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// The platform subscription directory and its revenue headline, for the System Admin portal.
///
/// Read-only. Changing somebody's plan, cancelling a subscription or adjusting a contract price
/// are commercial acts with invoicing consequences, and each already has its own path through
/// SubscriptionService with its own validation — putting a second, thinner one behind an admin
/// table would be two ways to do the same thing, one of them untested.
/// </summary>
public interface IAdminSubscriptionService
{
    Task<Result<AdminPagedResult<AdminSubscriptionSummaryDto>>> GetDirectoryAsync(
        AdminSubscriptionDirectoryQuery query,
        CancellationToken ct = default);

    Task<Result<AdminSubscriptionSummaryTotalsDto>> GetSummaryAsync(CancellationToken ct = default);
}

using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// The billing half of the admin workspace page: one read that leads the page, and the money
/// actions an operator takes on one tenant.
///
/// Every write takes a reason, is recorded in the platform audit log before it is saved, and is
/// abandoned when the record is refused. The actor is resolved from the token by the controller;
/// nothing in a request body can name one.
/// </summary>
public interface IAdminWorkspaceBillingService
{
    Task<Result<AdminWorkspaceBillingOverviewDto>> GetOverviewAsync(
        Guid workspaceId, AdminDateRange range, CancellationToken ct = default);

    Task<Result<AdminWorkspaceBillingActionResultDto>> AdjustCreditsAsync(
        Guid workspaceId, AdminAdjustWorkspaceCreditsRequest request, AdminActorContext actor, CancellationToken ct = default);

    Task<Result<AdminWorkspaceBillingActionResultDto>> ChangePlanAsync(
        Guid workspaceId, AdminWorkspaceChangePlanRequest request, AdminActorContext actor, CancellationToken ct = default);

    Task<Result<AdminWorkspaceBillingActionResultDto>> ExtendTrialAsync(
        Guid workspaceId, AdminExtendTrialRequest request, AdminActorContext actor, CancellationToken ct = default);

    Task<Result<AdminWorkspaceBillingActionResultDto>> CompPeriodAsync(
        Guid workspaceId, AdminCompPeriodRequest request, AdminActorContext actor, CancellationToken ct = default);

    Task<Result<AdminWorkspaceBillingActionResultDto>> SetEntitlementOverridesAsync(
        Guid workspaceId, AdminEntitlementOverridesRequest request, AdminActorContext actor, CancellationToken ct = default);

    Task<Result<AdminWorkspaceBillingActionResultDto>> MarkInvoicePaidAsync(
        Guid workspaceId, Guid invoiceId, AdminMarkInvoicePaidRequest request, AdminActorContext actor, CancellationToken ct = default);
}

using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Services;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services.PaymentEventHandlers;

/// <summary>
/// G11 — the life of a workspace add-on, driven by its own Stripe subscription:
///   AddOn             checkout completed → the workspace_addons row is created (granting)
///   AddOnRenewal      a renewal invoice paid → period extended, revenue added
///   AddOnUpdate       Stripe subscription updated → cancel-at-period-end / quantity mirrored
///   AddOnCancellation Stripe subscription deleted → stops granting
///
/// Every one of these is claimed here and none reaches the plan handlers: an add-on subscription
/// that ends must never end the workspace's plan (CancellationPaymentEventHandler would), and an
/// add-on renewal is not a plan renewal (SubscriptionPaymentEventHandler would look for a plan).
/// Each sets <see cref="PaymentEventContext.EntitlementsChanged"/>; the caller republishes the
/// entitlement snapshot after committing.
/// </summary>
public sealed class AddOnPaymentEventHandler : IPaymentEventHandler
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<AddOnPaymentEventHandler> _logger;

    public AddOnPaymentEventHandler(IUnitOfWork unitOfWork, ILogger<AddOnPaymentEventHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public bool CanHandle(PaymentEventContext context) =>
        PaymentConstants.PaymentTypes.AddOnLifecycleTypes.Contains(context.Request.PaymentType);

    public Task<Result> HandleAsync(PaymentEventContext context, CancellationToken cancellationToken = default) =>
        context.Request.PaymentType switch
        {
            PaymentConstants.PaymentTypes.AddOn => ActivateAsync(context, cancellationToken),
            PaymentConstants.PaymentTypes.AddOnRenewal => RenewAsync(context, cancellationToken),
            PaymentConstants.PaymentTypes.AddOnUpdate => UpdateAsync(context, cancellationToken),
            PaymentConstants.PaymentTypes.AddOnCancellation => CancelAsync(context, cancellationToken),
            _ => Task.FromResult(Result.Success()),
        };

    private async Task<Result> ActivateAsync(PaymentEventContext context, CancellationToken ct)
    {
        if (context.ParsedPaymentStatus != PaymentConstants.PaymentStatuses.Paid) return Result.Success();

        var request = context.Request;
        if (!string.IsNullOrWhiteSpace(request.StripeSessionId)
            && await _unitOfWork.WorkspaceAddons.ExistsForSessionAsync(request.StripeSessionId, ct))
        {
            return Result.Success();
        }

        if (!Guid.TryParse(request.PackageId, out var addonId)
            || await _unitOfWork.Addons.GetByIdAsync(addonId, ct) is not { } addon)
        {
            _logger.LogError("addon_not_found: PackageId={PackageId} StripeSessionId={SessionId}", request.PackageId, request.StripeSessionId);
            return Result.Failure(PackageCatalogConstants.Errors.NotFound, ErrorCodes.ValidationError);
        }

        // Stripe charged for exactly one live add-on of this kind; if a second checkout for the
        // same add-on raced the first to completion, the second is recorded against the first
        // rather than violating the one-open-row index and failing the whole payment.
        var existing = (await _unitOfWork.WorkspaceAddons.GetOpenForWorkspaceAsync(context.WorkspaceId, ct))
            .FirstOrDefault(row => row.AddonId == addon.Id);
        if (existing is not null)
        {
            _logger.LogError(
                "addon_duplicate_purchase: WorkspaceId={WorkspaceId} Addon={Slug} Session={SessionId}. A second Stripe subscription "
                + "{StripeSubscription} exists for an add-on already live; cancel one in Stripe and refund.",
                context.WorkspaceId, addon.Slug, request.StripeSessionId, request.StripeSubscriptionId);
            return Result.Success();
        }

        var now = DateTime.UtcNow;
        var quantity = Math.Max(1, request.Quantity);
        var cycle = BillingCycleResolver.ToPriceInterval(request.BillingCycle) == PaymentConstants.PriceIntervals.Year
            ? SubscriptionConstants.BillingCycles.Yearly
            : SubscriptionConstants.BillingCycles.Monthly;

        await _unitOfWork.WorkspaceAddons.AddAsync(new WorkspaceAddon
        {
            Id = Guid.NewGuid(),
            WorkspaceId = context.WorkspaceId,
            AddonId = addon.Id,
            UserId = context.UserId,
            Quantity = quantity,
            BillingCycle = cycle,
            Currency = (request.Currency ?? string.Empty).ToLowerInvariant(),
            UnitPrice = request.ListPrice > 0 ? decimal.Round(request.ListPrice / quantity, 2) : decimal.Round(request.Amount / quantity, 2),
            AmountBilledTotal = request.Amount,
            Status = PackageCatalogConstants.WorkspaceAddonStatuses.Active,
            StripeSubscriptionId = string.IsNullOrWhiteSpace(request.StripeSubscriptionId) ? null : request.StripeSubscriptionId,
            StripeSessionId = string.IsNullOrWhiteSpace(request.StripeSessionId) ? null : request.StripeSessionId,
            CouponId = Guid.TryParse(request.CouponId, out var couponId) ? couponId : null,
            CurrentPeriodEnd = request.PeriodEnd ?? BillingCycleResolver.AddOneCycle(now, cycle),
            StartedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        }, ct);

        context.EntitlementsChanged = true;
        _logger.LogInformation("addon_activated: Addon={Slug} Quantity={Quantity} WorkspaceId={WorkspaceId}", addon.Slug, quantity, context.WorkspaceId);
        return Result.Success();
    }

    private async Task<Result> RenewAsync(PaymentEventContext context, CancellationToken ct)
    {
        if (context.ParsedPaymentStatus != PaymentConstants.PaymentStatuses.Paid) return Result.Success();

        var row = await FindAsync(context, ct);
        if (row is null) return Result.Success();

        row.AmountBilledTotal += context.Request.Amount;
        row.CurrentPeriodEnd = context.Request.PeriodEnd ?? BillingCycleResolver.AddOneCycle(DateTime.UtcNow, row.BillingCycle);
        if (row.Status == PackageCatalogConstants.WorkspaceAddonStatuses.Cancelled)
        {
            // Stripe only bills a live subscription; a paid renewal means it is live.
            row.Status = PackageCatalogConstants.WorkspaceAddonStatuses.Active;
            row.CancelledAt = null;
        }

        row.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.WorkspaceAddons.Update(row);
        context.EntitlementsChanged = true;
        return Result.Success();
    }

    private async Task<Result> UpdateAsync(PaymentEventContext context, CancellationToken ct)
    {
        var row = await FindAsync(context, ct);
        if (row is null || row.Status == PackageCatalogConstants.WorkspaceAddonStatuses.Cancelled) return Result.Success();

        row.Status = context.Request.CancelAtPeriodEnd
            ? PackageCatalogConstants.WorkspaceAddonStatuses.Cancelling
            : PackageCatalogConstants.WorkspaceAddonStatuses.Active;
        if (context.Request.Quantity > 0) row.Quantity = context.Request.Quantity;
        if (context.Request.PeriodEnd is { } periodEnd) row.CurrentPeriodEnd = periodEnd;
        row.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.WorkspaceAddons.Update(row);
        context.EntitlementsChanged = true;
        return Result.Success();
    }

    private async Task<Result> CancelAsync(PaymentEventContext context, CancellationToken ct)
    {
        var row = await FindAsync(context, ct);
        if (row is null || row.Status == PackageCatalogConstants.WorkspaceAddonStatuses.Cancelled) return Result.Success();

        row.Status = PackageCatalogConstants.WorkspaceAddonStatuses.Cancelled;
        row.CancelledAt = DateTime.UtcNow;
        row.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.WorkspaceAddons.Update(row);
        context.EntitlementsChanged = true;
        _logger.LogInformation("addon_cancelled: WorkspaceAddon={Id} WorkspaceId={WorkspaceId}", row.Id, row.WorkspaceId);
        return Result.Success();
    }

    private async Task<WorkspaceAddon?> FindAsync(PaymentEventContext context, CancellationToken ct)
    {
        var subscriptionId = context.Request.StripeSubscriptionId;
        if (string.IsNullOrWhiteSpace(subscriptionId)) return null;

        var row = await _unitOfWork.WorkspaceAddons.GetByStripeSubscriptionIdAsync(subscriptionId, ct);
        if (row is null)
        {
            // An event for an add-on subscription we never recorded (the checkout completion has
            // not been processed yet, or failed). Nothing to change; the completion creates it.
            _logger.LogWarning(
                "addon_event_unmatched: {PaymentType} for Stripe subscription {Subscription} has no workspace add-on row.",
                context.Request.PaymentType, subscriptionId);
        }

        return row;
    }
}

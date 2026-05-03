using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services.Interface;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Application.Services;

public class SubscriptionService : ISubscriptionService
{
    private readonly ISubscriptionRepository _subscriptionRepo;
    private readonly ISubscriptionPlanRepository _planRepo;
    private readonly IUnitOfWork _uow;

    public SubscriptionService(
        ISubscriptionRepository subscriptionRepo,
        ISubscriptionPlanRepository planRepo,
        IUnitOfWork uow)
    {
        _subscriptionRepo = subscriptionRepo;
        _planRepo = planRepo;
        _uow = uow;
    }

    // ======================================================
    // GET ACTIVE
    // ======================================================
    public async Task<Subscription> GetActiveAsync(
        Guid workspaceId,
        CancellationToken ct = default)
    {
        var subscription = await _subscriptionRepo
            .GetActiveByWorkspaceIdAsync(workspaceId, ct);

        if (subscription == null)
            throw new Exception("No active subscription found");

        return subscription;
    }

    // ======================================================
    // CREATE
    // ======================================================
    public async Task<Subscription> CreateAsync(
        CreateSubscriptionCommand command,
        CancellationToken ct = default)
    {
        var plan = await _planRepo.GetByIdAsync(command.PlanId, ct);

        if (plan == null || !plan.IsActive)
            throw new Exception("Invalid plan");

        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            WorkspaceId = command.WorkspaceId,
            PlanId = plan.Id,
            Status = SubscriptionStatus.Active,
            StartDate = DateTime.UtcNow,
            EndDate = command.DurationDays.HasValue
                ? DateTime.UtcNow.AddDays(command.DurationDays.Value)
                : null
        };

        await _subscriptionRepo.AddAsync(subscription, ct);
        await _uow.SaveChangesAsync(ct);

        return subscription;
    }

    // ======================================================
    // UPGRADE
    // ======================================================
    public async Task UpgradeAsync(
        UpgradeSubscriptionCommand command,
        CancellationToken ct = default)
    {
        var subscription = await _subscriptionRepo
            .GetByIdAsync(command.SubscriptionId, ct);

        if (subscription == null)
            throw new Exception("Subscription not found");

        var plan = await _planRepo.GetByIdAsync(command.NewPlanId, ct);

        if (plan == null || !plan.IsActive)
            throw new Exception("Invalid plan");

        subscription.PlanId = plan.Id;
        subscription.UpdatedAt = DateTime.UtcNow;

        await _subscriptionRepo.UpdateAsync(subscription, ct);
        await _uow.SaveChangesAsync(ct);
    }

    // ======================================================
    // CANCEL
    // ======================================================
    public async Task CancelAsync(
        Guid subscriptionId,
        CancellationToken ct = default)
    {
        var subscription = await _subscriptionRepo
            .GetByIdAsync(subscriptionId, ct);

        if (subscription == null)
            throw new Exception("Subscription not found");

        subscription.Status = SubscriptionStatus.Cancelled;
        subscription.EndDate = DateTime.UtcNow;

        await _subscriptionRepo.UpdateAsync(subscription, ct);
        await _uow.SaveChangesAsync(ct);
    }
}
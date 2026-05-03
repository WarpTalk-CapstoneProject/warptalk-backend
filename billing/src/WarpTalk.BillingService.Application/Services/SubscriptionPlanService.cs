using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Services.Interface;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Application.Services;

public class SubscriptionPlanService : ISubscriptionPlanService
{
    private readonly ISubscriptionPlanRepository _repository;
    private readonly ILogger<SubscriptionPlanService> _logger;

    public SubscriptionPlanService(
        ISubscriptionPlanRepository repository,
        ILogger<SubscriptionPlanService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<SubscriptionPlan?> GetByIdAsync(
        Guid planId,
        CancellationToken cancellationToken = default)
    {
        return await _repository.GetByIdAsync(planId, cancellationToken);
    }

    public async Task<IReadOnlyList<SubscriptionPlan>> GetAllActiveAsync(
    CancellationToken cancellationToken = default)
    {
        return await _repository.GetAllActiveAsync(cancellationToken);
    }
    public async Task<bool> ExistsAsync(
        Guid planId,
        CancellationToken cancellationToken = default)
    {
        var plan = await _repository.GetByIdAsync(planId, cancellationToken);
        return plan != null;
    }
}
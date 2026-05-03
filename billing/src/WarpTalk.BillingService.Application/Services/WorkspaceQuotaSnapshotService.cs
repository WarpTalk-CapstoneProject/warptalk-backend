using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Services.Interface;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Application.Services;

public class WorkspaceQuotaSnapshotService : IWorkspaceQuotaSnapshotService
{
    private readonly IWorkspaceQuotaSnapshotRepository _repository;
    private readonly ISubscriptionPlanRepository _planRepository;
    private readonly ILogger<WorkspaceQuotaSnapshotService> _logger;

    public WorkspaceQuotaSnapshotService(
        IWorkspaceQuotaSnapshotRepository repository,
        ISubscriptionPlanRepository planRepository,
        ILogger<WorkspaceQuotaSnapshotService> logger)
    {
        _repository = repository;
        _planRepository = planRepository;
        _logger = logger;
    }

    // ======================================================
    // GET
    // ======================================================
    public async Task<WorkspaceQuotaSnapshot?> GetByWorkspaceIdAsync(
        Guid workspaceId,
        CancellationToken ct = default)
    {
        return await _repository.GetByWorkspaceIdAsync(workspaceId, ct);
    }

    // ======================================================
    // GET OR CREATE
    // ======================================================
    public async Task<WorkspaceQuotaSnapshot> GetOrCreateAsync(
        Guid workspaceId,
        Guid planId,
        CancellationToken ct = default)
    {
        var existing = await _repository.GetByWorkspaceIdAsync(workspaceId, ct);
        if (existing != null)
            return existing;

        var plan = await _planRepository.GetByIdAsync(planId, ct);
        if (plan == null)
            throw new InvalidOperationException("Subscription plan not found.");

        var snapshot = new WorkspaceQuotaSnapshot
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            CurrentBalance = plan.IncludedCredits,
            ReservedCredits = 0,
            LowCreditThreshold = plan.IncludedCredits * 0.1m,

            // FIX: use valid enum
            CurrentMode = QuotaMode.FullVoice,

            UpdatedAt = DateTime.UtcNow
        };

        await _repository.AddAsync(snapshot, ct);
        return snapshot;
    }

    // ======================================================
    // UPDATE
    // ======================================================
    public async Task UpdateSnapshotAsync(
        WorkspaceQuotaSnapshot snapshot,
        CancellationToken ct = default)
    {
        snapshot.UpdatedAt = DateTime.UtcNow;

        await _repository.UpdateAsync(snapshot, ct);

        _logger.LogInformation(
            "Updated quota snapshot for workspace {WorkspaceId}",
            snapshot.WorkspaceId);
    }
}
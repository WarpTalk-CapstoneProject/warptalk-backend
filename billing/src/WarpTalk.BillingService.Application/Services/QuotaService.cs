using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services.Interface;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Domain.Interfaces;

public class QuotaService : IQuotaService
{
    private readonly IWorkspaceQuotaSnapshotRepository _snapshotRepo;
    private readonly IQuotaAuditLogRepository _auditRepo;
    private readonly ISubscriptionPlanRepository _planRepo;
    private readonly IUnitOfWork _uow;

    public QuotaService(
        IWorkspaceQuotaSnapshotRepository snapshotRepo,
        IQuotaAuditLogRepository auditRepo,
        ISubscriptionPlanRepository planRepo,
        IUnitOfWork uow)
    {
        _snapshotRepo = snapshotRepo;
        _auditRepo = auditRepo;
        _planRepo = planRepo;
        _uow = uow;
    }

    // ================= CHECK =================
    public async Task<QuotaCheckResponse> CheckQuotaByOwnerAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        var snapshot = await _snapshotRepo.GetByWorkspaceIdAsync(ownerUserId, ct);

        return new QuotaCheckResponse
        {
            OwnerUserId = ownerUserId,
            Balance = snapshot?.CurrentBalance ?? 0
        };
    }

    // ================= TOPUP =================
    public async Task<QuotaTopUpResponse> TopUpQuotaByOwnerAsync(Guid ownerUserId, decimal credits, string? referenceId = null, CancellationToken ct = default)
    {
        var snapshot = await _snapshotRepo.GetByWorkspaceIdAsync(ownerUserId, ct)
            ?? new WorkspaceQuotaSnapshot
            {
                Id = Guid.NewGuid(),
                WorkspaceId = ownerUserId,
                CurrentBalance = 0,
                ReservedCredits = 0
            };

        snapshot.CurrentBalance += credits;

        await _snapshotRepo.AddAsync(snapshot, ct);

        await _auditRepo.AddAsync(new QuotaAuditLog
        {
            Id = Guid.NewGuid(),
            WorkspaceId = ownerUserId,
            Action = AuditAction.TopUp
        }, ct);

        await _uow.SaveChangesAsync(ct);

        return new QuotaTopUpResponse
        {
            NewBalance = snapshot.CurrentBalance
        };
    }

    // ================= DEDUCT =================
    public async Task<QuotaDeductResponse> DeductAsync(Guid ownerUserId, decimal credits, CancellationToken ct = default)
    {
        var snapshot = await _snapshotRepo.GetByWorkspaceIdAsync(ownerUserId, ct);

        if (snapshot == null || snapshot.CurrentBalance < credits)
        {
            return new QuotaDeductResponse
            {
                Success = false,
                ErrorCode = "INSUFFICIENT_QUOTA"
            };
        }

        snapshot.CurrentBalance -= credits;

        await _auditRepo.AddAsync(new QuotaAuditLog
        {
            Id = Guid.NewGuid(),
            WorkspaceId = ownerUserId,
            Action = AuditAction.Deduct
        }, ct);

        await _snapshotRepo.UpdateAsync(snapshot, ct);
        await _uow.SaveChangesAsync(ct);

        return new QuotaDeductResponse
        {
            Success = true,
            NewBalance = snapshot.CurrentBalance
        };
    }

    // ================= REFUND =================
    public async Task<QuotaRefundResponse> RefundAsync(Guid ownerUserId, decimal credits, CancellationToken ct = default)
    {
        var snapshot = await _snapshotRepo.GetByWorkspaceIdAsync(ownerUserId, ct)
            ?? throw new Exception("Snapshot not found");

        snapshot.CurrentBalance += credits;

        await _auditRepo.AddAsync(new QuotaAuditLog
        {
            Id = Guid.NewGuid(),
            WorkspaceId = ownerUserId,
            Action = AuditAction.Refund
        }, ct);

        await _snapshotRepo.UpdateAsync(snapshot, ct);
        await _uow.SaveChangesAsync(ct);

        return new QuotaRefundResponse
        {
            Success = true,
            NewBalance = snapshot.CurrentBalance
        };
    }

    // ================= PLANS =================
    public async Task<IReadOnlyList<SubscriptionPlan>> GetPlansAsync(CancellationToken ct = default)
    {
        return await _planRepo.GetAllActiveAsync(ct);
    }

    // ================= UPGRADE =================
    public async Task<bool> UpgradePlanByOwnerAsync(Guid ownerUserId, Guid planId, CancellationToken ct = default)
    {
        var plan = await _planRepo.GetByIdAsync(planId, ct)
            ?? throw new Exception("Plan not found");

        var snapshot = await _snapshotRepo.GetByWorkspaceIdAsync(ownerUserId, ct)
            ?? new WorkspaceQuotaSnapshot
            {
                Id = Guid.NewGuid(),
                WorkspaceId = ownerUserId,
                CurrentBalance = 0,
                ReservedCredits = 0
            };

        snapshot.CurrentBalance += plan.IncludedCredits;

        await _snapshotRepo.AddAsync(snapshot, ct);

        await _auditRepo.AddAsync(new QuotaAuditLog
        {
            Id = Guid.NewGuid(),
            WorkspaceId = ownerUserId,
            Action = AuditAction.PurchasePlan
        }, ct);

        await _uow.SaveChangesAsync(ct);

        return true;
    }

    Task<QuotaCheckResponse> IQuotaService.CheckQuotaByOwnerAsync(Guid ownerUserId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Enums;
using WarpTalk.BillingService.Domain.Interfaces;

using System.Collections.Generic;
using System.Linq;

namespace WarpTalk.BillingService.Application.Services;

public class QuotaService : IQuotaService
{
    private readonly IUsageQuotaRepository _quotaRepository;
    private readonly IQuotaAuditLogRepository _auditLogRepository;
    private readonly ISubscriptionPlanRepository _planRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<QuotaService> _logger;

    public QuotaService(
        IUsageQuotaRepository quotaRepository,
        IQuotaAuditLogRepository auditLogRepository,
        ISubscriptionPlanRepository planRepository,
        IUnitOfWork unitOfWork,
        ILogger<QuotaService> logger)
    {
        _quotaRepository = quotaRepository;
        _auditLogRepository = auditLogRepository;
        _planRepository = planRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<QuotaCheckResponse> CheckQuotaAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var quota = await _quotaRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        if (quota == null)
        {
            _logger.LogWarning("Workspace {WorkspaceId} not found or has no active quota.", workspaceId);
            return new QuotaCheckResponse(false, string.Empty, "None", 0, 0, new { });
        }

        var remaining = quota.TotalAllocatedMinutes - quota.ConsumedMinutes;
        var hasQuota = remaining > 0;

        return new QuotaCheckResponse(
            hasQuota,
            quota.PlanId.ToString(),
            quota.Plan?.Name.ToString() ?? "Unknown",
            remaining,
            quota.Plan?.MaxParticipants ?? 0,
            quota.Plan?.FeaturesJson ?? "{}"
        );
    }

    public async Task<QuotaDeductResponse> DeductQuotaAsync(Guid workspaceId, QuotaDeductRequest request, CancellationToken cancellationToken = default)
    {
        var quota = await _quotaRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        if (quota == null)
        {
            return new QuotaDeductResponse(false, 0, "QuotaNotFound");
        }

        var remaining = quota.TotalAllocatedMinutes - quota.ConsumedMinutes;
        if (remaining < request.ConsumedMinutes)
        {
            return new QuotaDeductResponse(false, remaining, "InsufficientQuota");
        }

        try
        {
            quota.ConsumedMinutes += request.ConsumedMinutes;
            await _quotaRepository.UpdateAsync(quota, cancellationToken);

            var auditLog = new QuotaAuditLog
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                Action = AuditAction.Deduct,
                Amount = request.ConsumedMinutes,
                BalanceAfter = quota.TotalAllocatedMinutes - quota.ConsumedMinutes,
                ReferenceId = request.SessionId.ToString(),
                Description = $"Deducted {request.ConsumedMinutes} mins for session {request.SessionId}",
                CreatedAt = DateTime.UtcNow
            };
            await _auditLogRepository.AddAsync(auditLog, cancellationToken);


            await _unitOfWork.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Successfully deducted {Amount} mins for Workspace {WorkspaceId}. Remaining: {Remaining}", 
                request.ConsumedMinutes, workspaceId, auditLog.BalanceAfter);

            return new QuotaDeductResponse(true, auditLog.BalanceAfter);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogError("Concurrency conflict deducting quota for Workspace {WorkspaceId}", workspaceId);
            return new QuotaDeductResponse(false, remaining, "ConcurrencyConflict");
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_QuotaAuditLogs_ReferenceId") == true)
        {
            _logger.LogWarning("Session {SessionId} already processed for Workspace {WorkspaceId}", request.SessionId, workspaceId);

            return new QuotaDeductResponse(false, remaining, "IdempotentRequestAlreadyProcessed");
        }
    }

    public async Task<QuotaDeductResponse> RefundQuotaAsync(Guid workspaceId, QuotaRefundRequest request, CancellationToken cancellationToken = default)
    {
        var quota = await _quotaRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        if (quota == null)
        {
            return new QuotaDeductResponse(false, 0, "QuotaNotFound");
        }

        try
        {
            // Refund = decrease consumed minutes
            quota.ConsumedMinutes = Math.Max(0, quota.ConsumedMinutes - request.RefundedMinutes);
            await _quotaRepository.UpdateAsync(quota, cancellationToken);

            var auditLog = new QuotaAuditLog
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                Action = AuditAction.Refund,
                Amount = request.RefundedMinutes,
                BalanceAfter = quota.TotalAllocatedMinutes - quota.ConsumedMinutes,
                ReferenceId = $"REFUND_{request.SessionId}",
                Description = request.Reason ?? $"Refunded {request.RefundedMinutes} mins for session {request.SessionId}",
                CreatedAt = DateTime.UtcNow
            };
            await _auditLogRepository.AddAsync(auditLog, cancellationToken);


            await _unitOfWork.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Successfully refunded {Amount} mins for Workspace {WorkspaceId}. New Balance: {Remaining}", 
                request.RefundedMinutes, workspaceId, auditLog.BalanceAfter);

            return new QuotaDeductResponse(true, auditLog.BalanceAfter);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogError("Concurrency conflict refunding quota for Workspace {WorkspaceId}", workspaceId);
            return new QuotaDeductResponse(false, quota.TotalAllocatedMinutes - quota.ConsumedMinutes, "ConcurrencyConflict");
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_QuotaAuditLogs_ReferenceId") == true)
        {
            _logger.LogWarning("Refund for Session {SessionId} already processed for Workspace {WorkspaceId}", request.SessionId, workspaceId);

            return new QuotaDeductResponse(false, quota.TotalAllocatedMinutes - quota.ConsumedMinutes, "IdempotentRequestAlreadyProcessed");
        }
    }

    public async Task<IEnumerable<SubscriptionPlan>> GetAvailablePlansAsync(CancellationToken cancellationToken = default)
    {
        return await _planRepository.GetAllActiveAsync(cancellationToken);
    }

    public async Task<bool> UpgradePlanAsync(Guid workspaceId, Guid planId, CancellationToken cancellationToken = default)
    {
        var quota = await _quotaRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        if (quota == null) return false;

        var plan = await _planRepository.GetByIdAsync(planId, cancellationToken);
        if (plan == null) return false;

        quota.PlanId = planId;
        // Logic: When upgrading, we reset the allocated minutes to the new plan's base quota
        // (Or we could add it, but usually, a plan change resets the monthly limit)
        quota.TotalAllocatedMinutes = plan.BaseQuotaMinutes;
        quota.ConsumedMinutes = 0; // Reset usage for the new plan period

        await _quotaRepository.UpdateAsync(quota, cancellationToken);

        var auditLog = new QuotaAuditLog
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Action = AuditAction.UpgradePlan,
            Amount = plan.BaseQuotaMinutes,
            BalanceAfter = plan.BaseQuotaMinutes,
            ReferenceId = $"UPGRADE_{workspaceId}_{DateTime.UtcNow.Ticks}",
            Description = $"Upgraded to plan {plan.Name}",
            CreatedAt = DateTime.UtcNow
        };
        await _auditLogRepository.AddAsync(auditLog, cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

}

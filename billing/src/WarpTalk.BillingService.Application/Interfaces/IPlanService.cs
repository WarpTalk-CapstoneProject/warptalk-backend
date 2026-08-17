using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IPlanService
{
    /// <summary>Customer-facing catalogue: active plans only (BR-74).</summary>
    Task<Result<IEnumerable<PlanDto>>> GetActivePlansAsync(CancellationToken cancellationToken = default);

    /// <summary>Every plan including deactivated ones. System Admin only.</summary>
    Task<Result<IEnumerable<PlanDto>>> GetAllPlansAsync(CancellationToken cancellationToken = default);
    Task<Result<PlanDto>> GetPlanByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<PlanDto>> UpdatePlanAsync(Guid id, PlanRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// New catalogue entry, System Admin only. Same validation as UpdatePlanAsync. There is still
    /// no delete: a retired plan is <c>IsActive = false</c>, because it keeps appearing on old
    /// invoices.
    /// </summary>
    Task<Result<PlanDto>> CreatePlanAsync(PlanRequest request, CancellationToken cancellationToken = default);
}

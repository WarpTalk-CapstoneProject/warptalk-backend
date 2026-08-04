using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IPlanService
{
    Task<Result<IEnumerable<PlanDto>>> GetActivePlansAsync(CancellationToken cancellationToken = default);
    Task<Result<PlanDto>> GetPlanByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<PlanDto>> UpdatePlanAsync(Guid id, PlanRequest request, CancellationToken cancellationToken = default);
}

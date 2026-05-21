using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IPlanService
{
    Task<Result<IEnumerable<PlanDto>>> GetActivePlansAsync(
        CancellationToken cancellationToken = default);

    Task<Result<PlanDto>> GetPlanByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Result<PlanDto>> GetPlanBySlugAsync(
        string slug,
        CancellationToken cancellationToken = default);
}

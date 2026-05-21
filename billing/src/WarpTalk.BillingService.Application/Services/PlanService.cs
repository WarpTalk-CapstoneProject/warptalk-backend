using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class PlanService : IPlanService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<PlanService> _logger;

    public PlanService(IUnitOfWork unitOfWork, ILogger<PlanService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<IEnumerable<PlanDto>>> GetActivePlansAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var plans = await _unitOfWork.PlanRepository.FindAsync(
                p => p.IsActive && p.DeletedAt == null,
                cancellationToken);

            return Result.Success(plans.Select(p => p.ToDto()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active plans");
            return Result.Failure<IEnumerable<PlanDto>>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<PlanDto>> GetPlanByIdAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var plan = await _unitOfWork.PlanRepository.FirstOrDefaultAsync(
                p => p.Id == id && p.DeletedAt == null,
                cancellationToken);

            if (plan is null)
                return Result.Failure<PlanDto>(
                    $"Plan '{id}' not found.",
                    ErrorCodes.BillingPlanNotFound);

            return Result.Success(plan.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting plan by Id {PlanId}", id);
            return Result.Failure<PlanDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    public async Task<Result<PlanDto>> GetPlanBySlugAsync(
        string slug, CancellationToken cancellationToken = default)
    {
        try
        {
            var plan = await _unitOfWork.PlanRepository.FirstOrDefaultAsync(
                p => p.Slug == slug && p.DeletedAt == null,
                cancellationToken);

            if (plan is null)
                return Result.Failure<PlanDto>(
                    $"Plan with slug '{slug}' not found.",
                    ErrorCodes.BillingPlanNotFound);

            return Result.Success(plan.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting plan by Slug {Slug}", slug);
            return Result.Failure<PlanDto>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

public class PlanService : IPlanService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<PlanService> _logger;
    private readonly IBillingMessagePublisher _messagePublisher;

    public PlanService(
        IUnitOfWork unitOfWork,
        ILogger<PlanService> logger,
        IBillingMessagePublisher messagePublisher)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _messagePublisher = messagePublisher;
    }

    public async Task<Result<IEnumerable<PlanDto>>> GetActivePlansAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var plans = await _unitOfWork.PlanRepository.FindAsync(
                p => p.DeletedAt == null,
                cancellationToken);

            return Result.Success(plans.Select(p => p.ToDto()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting plans");
            return Result.Failure<IEnumerable<PlanDto>>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
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
                    ApiMessageConstants.ErrorMessages.BillingPlanNotFound,
                    ErrorCodes.BillingPlanNotFound);

            return Result.Success(plan.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting plan by Id {PlanId}", id);
            return Result.Failure<PlanDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
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
                    ApiMessageConstants.ErrorMessages.BillingPlanNotFound,
                    ErrorCodes.BillingPlanNotFound);

            return Result.Success(plan.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting plan by Slug {Slug}", slug);
            return Result.Failure<PlanDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    private Result<PlanDto> ValidatePlanRequest(PlanRequest request)
    {
        var currency = request.Currency?.ToUpperInvariant().Trim() ?? "";
        decimal minPrice = 0.50m; // Stripe minimum for USD

        var cycle = request.BillingCycle?.ToLowerInvariant().Trim();
        bool isInvalidCycle = cycle is not (BillingConstants.BillingCycles.Monthly or 
                                            BillingConstants.BillingCycles.Semiannual or 
                                            BillingConstants.BillingCycles.Yearly);
        bool isInvalidFeatures = !string.IsNullOrWhiteSpace(request.Features) && 
                                 !(request.Features.Trim().StartsWith("{") && request.Features.Trim().EndsWith("}")) && 
                                 !(request.Features.Trim().StartsWith("[") && request.Features.Trim().EndsWith("]"));

        var validations = new (bool IsInvalid, string ErrorMessage)[]
        {
            (string.IsNullOrWhiteSpace(request.Name), ApiMessageConstants.ValidationMessages.PlanNameRequired),
            (request.Name?.Length > 100, ApiMessageConstants.ValidationMessages.PlanNameMaxLength),
            
            (string.IsNullOrWhiteSpace(request.Slug), ApiMessageConstants.ValidationMessages.PlanSlugRequired),
            (request.Slug?.Length > 50, ApiMessageConstants.ValidationMessages.PlanSlugMaxLength),
            (!string.IsNullOrWhiteSpace(request.Slug) && !System.Text.RegularExpressions.Regex.IsMatch(request.Slug, "^[a-z0-9]+(?:-[a-z0-9]+)*$"), ApiMessageConstants.ValidationMessages.PlanSlugInvalid),
            
            (string.IsNullOrWhiteSpace(request.Tier), ApiMessageConstants.ValidationMessages.PlanTierRequired),
            (request.Tier?.Length > 20, ApiMessageConstants.ValidationMessages.PlanTierMaxLength),
            
            (string.IsNullOrWhiteSpace(request.Currency), ApiMessageConstants.ValidationMessages.PlanCurrencyRequired),
            (currency != "USD", ApiMessageConstants.ValidationMessages.PlanCurrencyInvalid),
            
            (string.IsNullOrWhiteSpace(request.BillingCycle), ApiMessageConstants.ValidationMessages.PlanBillingCycleRequired),
            (!string.IsNullOrWhiteSpace(request.BillingCycle) && isInvalidCycle, ApiMessageConstants.ValidationMessages.PlanBillingCycleInvalid),
            
            (request.Price < minPrice, string.Format(ApiMessageConstants.ValidationMessages.PlanMinPrice, currency, minPrice)),
            (request.CreditsPerCycle < 0, ApiMessageConstants.ValidationMessages.PlanCreditsPerCycleInvalid),
            (request.MaxParticipants < 2, ApiMessageConstants.ValidationMessages.PlanMaxParticipantsInvalid),
            (request.SortOrder < 0, ApiMessageConstants.ValidationMessages.PlanSortOrderInvalid),
            (isInvalidFeatures, ApiMessageConstants.ValidationMessages.PlanFeaturesInvalid)
        };

        var error = validations.FirstOrDefault(v => v.IsInvalid);
        if (error.IsInvalid)
            return Result.Failure<PlanDto>(error.ErrorMessage, ErrorCodes.ValidationError);

        return Result.Success<PlanDto>(null!);
    }

    public async Task<Result<PlanDto>> CreatePlanAsync(
        PlanRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var validationResult = ValidatePlanRequest(request);
            if (!validationResult.IsSuccess)
                return validationResult;

            var normalizedSlug = request.Slug.ToLowerInvariant().Trim();
            var existing = await _unitOfWork.PlanRepository.FirstOrDefaultAsync(
                p => p.Slug == normalizedSlug && p.DeletedAt == null,
                cancellationToken);

            if (existing is not null)
                return Result.Failure<PlanDto>(ApiMessageConstants.ErrorMessages.BillingDuplicatePlanSlug, ErrorCodes.BillingDuplicatePlanSlug);

            var plan = request.ToEntity();
            await _unitOfWork.PlanRepository.AddAsync(plan, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await PublishPlanUpdateNotificationAsync("created", plan.Name, null, cancellationToken);

            return Result.Success(plan.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating plan");
            return Result.Failure<PlanDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
     }

    public async Task<Result<PlanDto>> UpdatePlanAsync(
        Guid id, PlanRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var plan = await _unitOfWork.PlanRepository.FirstOrDefaultAsync(
                p => p.Id == id && p.DeletedAt == null,
                cancellationToken);

            if (plan is null)
                return Result.Failure<PlanDto>(ApiMessageConstants.ErrorMessages.BillingPlanNotFound, ErrorCodes.BillingPlanNotFound);

            var validationResult = ValidatePlanRequest(request);
            if (!validationResult.IsSuccess)
                return validationResult;

            var normalizedSlug = request.Slug.ToLowerInvariant().Trim();
            if (plan.Slug != normalizedSlug)
            {
                var existing = await _unitOfWork.PlanRepository.FirstOrDefaultAsync(
                    p => p.Slug == normalizedSlug && p.Id != id && p.DeletedAt == null,
                    cancellationToken);

                if (existing is not null)
                    return Result.Failure<PlanDto>(ApiMessageConstants.ErrorMessages.BillingDuplicatePlanSlug, ErrorCodes.BillingDuplicatePlanSlug);
            }

            var changes = new List<string>();

            if (plan.Price != request.Price)
                changes.Add(string.Format(BillingConstants.PlanAuditMessages.PriceChanged, plan.Price, request.Price, plan.Currency));

            AuditHelper.Track(changes, plan.CreditsPerCycle, request.CreditsPerCycle, BillingConstants.PlanAuditMessages.CreditsChanged);
            AuditHelper.Track(changes, plan.MaxParticipants, request.MaxParticipants, BillingConstants.PlanAuditMessages.MaxParticipantsChanged);
            AuditHelper.Track(changes, plan.Name, request.Name, BillingConstants.PlanAuditMessages.NameChanged);

            string? changeDetail = changes.Any() ? string.Join("; ", changes) : null;

            plan.UpdateFromRequest(request);
            _unitOfWork.PlanRepository.Update(plan);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await PublishPlanUpdateNotificationAsync("updated", plan.Name, changeDetail, cancellationToken);

            return Result.Success(plan.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating plan");
            return Result.Failure<PlanDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<bool>> DeactivatePlanAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var plan = await _unitOfWork.PlanRepository.FirstOrDefaultAsync(
                p => p.Id == id && p.DeletedAt == null,
                cancellationToken);

            if (plan is null)
                return Result.Failure<bool>(ApiMessageConstants.ErrorMessages.BillingPlanNotFound, ErrorCodes.BillingPlanNotFound);

            plan.IsActive = false;
            plan.DeletedAt = DateTime.UtcNow;
            _unitOfWork.PlanRepository.Update(plan);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await PublishPlanUpdateNotificationAsync("deactivated", plan.Name, null, cancellationToken);

            return Result.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deactivating plan");
            return Result.Failure<bool>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    private async Task PublishPlanUpdateNotificationAsync(string action, string planName, string? details = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var msg = NotificationMapper.ToPlanChangedMessage(action, planName, details);
            await _messagePublisher.PublishAsync(BillingConstants.Notifications.Channel, msg, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, BillingConstants.LogMessages.FailedToPublishPlanUpdateBroadcast, planName);
        }
    }
}

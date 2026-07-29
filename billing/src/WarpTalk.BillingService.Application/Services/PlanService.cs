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
            var plans = (await _unitOfWork.PlanRepository.FindAsync(
                p => p.DeletedAt == null,
                cancellationToken)).ToList();

            if (!plans.Any())
            {
                var defaultEnterprisePlan = new Plan
                {
                    Name = "Enterprise",
                    Slug = "enterprise",
                    Tier = "enterprise",
                    Price = 1900000m,
                    Currency = "VND",
                    BillingCycle = "monthly",
                    CreditsPerCycle = 700000,
                    OverageCapCredits = 105000,
                    OveragePricePerCredit = 4m,
                    LowBalanceThresholdCredits = 140000,
                    RolloverCapCredits = 700000,
                    InvoiceTermsDays = 15,
                    InvoiceGraceHours = 360,
                    MaxParticipants = 500,
                    MaxLanguages = 3,
                    VoiceCloneEnabled = true,
                    AiAssistantEnabled = true,
                    GlossaryEnabled = true,
                    DedicatedGpu = false,
                    Features = SubscriptionConstants.FeatureAccess.EnterpriseFeaturesJson,
                    SortOrder = 1,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                await _unitOfWork.PlanRepository.AddAsync(defaultEnterprisePlan, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                plans.Add(defaultEnterprisePlan);
            }

            return Result.Success(plans.Select(p => p.ToDto()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingPlans);
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
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingPlanById, id);
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
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorGettingPlanBySlug, slug);
            return Result.Failure<PlanDto>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<PlanDto>> CreatePlanAsync(
        PlanRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var validationResult = PlanHelper.ValidatePlanRequest(request);
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

            await BillingNotificationHelper.PublishPlanUpdateAsync(
                _messagePublisher,
                _logger,
                BillingMessageConstants.Plan.Actions.Created,
                plan.Name,
                null,
                cancellationToken);

            return Result.Success(plan.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorCreatingPlan);
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

            var validationResult = PlanHelper.ValidatePlanRequest(request);
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
                changes.Add(string.Format(BillingMessageConstants.PlanAuditMessages.PriceChanged, plan.Price, request.Price, plan.Currency));

            AuditHelper.Track(changes, plan.CreditsPerCycle, request.CreditsPerCycle, BillingMessageConstants.PlanAuditMessages.CreditsChanged);
            AuditHelper.Track(changes, plan.MaxParticipants, request.MaxParticipants, BillingMessageConstants.PlanAuditMessages.MaxParticipantsChanged);
            AuditHelper.Track(changes, plan.Name, request.Name, BillingMessageConstants.PlanAuditMessages.NameChanged);

            string? changeDetail = changes.Any() ? string.Join("; ", changes) : null;

            plan.UpdateFromRequest(request);
            _unitOfWork.PlanRepository.Update(plan);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await BillingNotificationHelper.PublishPlanUpdateAsync(
                _messagePublisher,
                _logger,
                BillingMessageConstants.Plan.Actions.Updated,
                plan.Name,
                changeDetail,
                cancellationToken);

            return Result.Success(plan.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorUpdatingPlan);
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

            await BillingNotificationHelper.PublishPlanUpdateAsync(
                _messagePublisher,
                _logger,
                BillingMessageConstants.Plan.Actions.Deactivated,
                plan.Name,
                null,
                cancellationToken);

            return Result.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, BillingMessageConstants.LogMessages.ErrorDeactivatingPlan);
            return Result.Failure<bool>(ApiMessageConstants.ErrorMessages.BillingInternalError, ErrorCodes.InternalServerError);
        }
    }

}

using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Entities;
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

    private Result<PlanDto> ValidatePlanRequest(PlanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Result.Failure<PlanDto>("Plan name is required.", "INVALID_REQUEST");
        if (request.Name.Length > 100)
            return Result.Failure<PlanDto>("Plan name must not exceed 100 characters.", "INVALID_REQUEST");

        if (string.IsNullOrWhiteSpace(request.Slug))
            return Result.Failure<PlanDto>("Slug is required.", "INVALID_REQUEST");
        if (request.Slug.Length > 50)
            return Result.Failure<PlanDto>("Slug must not exceed 50 characters.", "INVALID_REQUEST");
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.Slug, "^[a-z0-9]+(?:-[a-z0-9]+)*$"))
            return Result.Failure<PlanDto>("Slug must be lowercase alphanumeric characters and hyphens only (e.g., 'gold-tier').", "INVALID_REQUEST");

        if (string.IsNullOrWhiteSpace(request.Tier))
            return Result.Failure<PlanDto>("Tier is required.", "INVALID_REQUEST");
        if (request.Tier.Length > 20)
            return Result.Failure<PlanDto>("Tier must not exceed 20 characters.", "INVALID_REQUEST");

        if (string.IsNullOrWhiteSpace(request.Currency))
            return Result.Failure<PlanDto>("Currency is required.", "INVALID_REQUEST");
        var currency = request.Currency.ToUpperInvariant().Trim();
        if (currency.Length != 3)
            return Result.Failure<PlanDto>("Currency must be a 3-character ISO code.", "INVALID_REQUEST");

        if (string.IsNullOrWhiteSpace(request.BillingCycle))
            return Result.Failure<PlanDto>("Billing cycle is required.", "INVALID_REQUEST");
        var billingCycle = request.BillingCycle.ToLowerInvariant().Trim();
        if (billingCycle != "monthly" && billingCycle != "semiannual" && billingCycle != "yearly")
            return Result.Failure<PlanDto>("Billing cycle must be 'monthly', 'semiannual', or 'yearly'.", "INVALID_REQUEST");

        // Stripe Minimum Charge Limits Validation
        decimal minPrice = currency switch
        {
            "USD" => 0.50m,
            "EUR" => 0.50m,
            "GBP" => 0.30m,
            "SGD" => 0.50m,
            "CAD" => 0.50m,
            "AUD" => 0.50m,
            "VND" => 15000m,
            "JPY" => 50m,
            _ => 0.50m // Default minimum
        };

        if (request.Price < minPrice)
            return Result.Failure<PlanDto>($"Price for {currency} must be at least {minPrice} due to Stripe payment constraints.", "INVALID_REQUEST");

        if (request.CreditsPerCycle < 0)
            return Result.Failure<PlanDto>("Credits per cycle must be non-negative.", "INVALID_REQUEST");

        if (request.MaxParticipants < 2)
            return Result.Failure<PlanDto>("Max participants must be at least 2.", "INVALID_REQUEST");

        if (request.MaxLanguages < 1)
            return Result.Failure<PlanDto>("Max languages must be at least 1.", "INVALID_REQUEST");

        if (request.SortOrder < 0)
            return Result.Failure<PlanDto>("Sort order must be non-negative.", "INVALID_REQUEST");

        if (!string.IsNullOrWhiteSpace(request.Features))
        {
            var trimmedFeatures = request.Features.Trim();
            if (!((trimmedFeatures.StartsWith("{") && trimmedFeatures.EndsWith("}")) || (trimmedFeatures.StartsWith("[") && trimmedFeatures.EndsWith("]"))))
            {
                return Result.Failure<PlanDto>("Features must be a valid JSON string.", "INVALID_REQUEST");
            }
        }

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
                return Result.Failure<PlanDto>("A plan with this slug already exists.", "DUPLICATE_SLUG");

            var plan = request.ToEntity();
            await _unitOfWork.PlanRepository.AddAsync(plan, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            await PublishPlanUpdateNotificationAsync("created", plan.Name, null, cancellationToken);

            return Result.Success(plan.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating plan");
            return Result.Failure<PlanDto>("An unexpected error occurred.", "INTERNAL_ERROR");
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
                return Result.Failure<PlanDto>("Plan not found.", ErrorCodes.BillingPlanNotFound);

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
                    return Result.Failure<PlanDto>("A plan with this slug already exists.", "DUPLICATE_SLUG");
            }

            var changes = new List<string>();

            if (plan.Price != request.Price)
                changes.Add($"Price changed from {plan.Price:N0} to {request.Price:N0} {plan.Currency}");
            if (plan.CreditsPerCycle != request.CreditsPerCycle)
                changes.Add($"Credits per cycle changed from {plan.CreditsPerCycle:N0} to {request.CreditsPerCycle:N0}");
            if (plan.MaxParticipants != request.MaxParticipants)
                changes.Add($"Max participants changed from {plan.MaxParticipants} to {request.MaxParticipants}");
            if (plan.MaxLanguages != request.MaxLanguages)
                changes.Add($"Max languages changed from {plan.MaxLanguages} to {request.MaxLanguages}");
            if (plan.VoiceCloneEnabled != request.VoiceCloneEnabled)
                changes.Add($"Voice Cloning is now {(request.VoiceCloneEnabled ? "enabled" : "disabled")}");
            if (plan.AiAssistantEnabled != request.AiAssistantEnabled)
                changes.Add($"AI Assistant is now {(request.AiAssistantEnabled ? "enabled" : "disabled")}");
            if (plan.GlossaryEnabled != request.GlossaryEnabled)
                changes.Add($"Glossary is now {(request.GlossaryEnabled ? "enabled" : "disabled")}");
            if (plan.DedicatedGpu != request.DedicatedGpu)
                changes.Add($"Dedicated GPU is now {(request.DedicatedGpu ? "enabled" : "disabled")}");
            if (plan.Name != request.Name)
                changes.Add($"Name changed from '{plan.Name}' to '{request.Name}'");

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
            return Result.Failure<PlanDto>("An unexpected error occurred.", "INTERNAL_ERROR");
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
                return Result.Failure<bool>("Plan not found.", ErrorCodes.BillingPlanNotFound);

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
            return Result.Failure<bool>("An unexpected error occurred.", "INTERNAL_ERROR");
        }
    }

    private async Task PublishPlanUpdateNotificationAsync(string action, string planName, string? details = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var content = $"The subscription package '{planName}' has been {action}.";
            if (!string.IsNullOrWhiteSpace(details))
            {
                content += $" Details: {details}";
            }

            var msg = new WarpTalk.Shared.Models.RealtimeNotificationMessage
            {
                Id = Guid.NewGuid().ToString(),
                UserId = "all",
                Type = "billing.plan_changed",
                Title = "System Plan Update",
                Content = content,
                PayloadJson = "{}"
            };
            await _messagePublisher.PublishAsync("warptalk:notifications:new", msg, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish plan update broadcast for plan {PlanName}", planName);
        }
    }
}

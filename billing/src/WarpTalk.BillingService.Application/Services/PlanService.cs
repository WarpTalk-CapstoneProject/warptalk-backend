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

    public async Task<Result<PlanDto>> CreatePlanAsync(
        PlanRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Result.Failure<PlanDto>("Plan name is required.", "INVALID_REQUEST");

            if (string.IsNullOrWhiteSpace(request.Slug))
                return Result.Failure<PlanDto>("Slug is required.", "INVALID_REQUEST");

            if (request.Price < 0)
                return Result.Failure<PlanDto>("Price must be non-negative.", "INVALID_REQUEST");

            if (request.CreditsPerCycle < 0)
                return Result.Failure<PlanDto>("Credits per cycle must be non-negative.", "INVALID_REQUEST");

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

            if (string.IsNullOrWhiteSpace(request.Name))
                return Result.Failure<PlanDto>("Plan name is required.", "INVALID_REQUEST");

            if (request.Price < 0)
                return Result.Failure<PlanDto>("Price must be non-negative.", "INVALID_REQUEST");

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

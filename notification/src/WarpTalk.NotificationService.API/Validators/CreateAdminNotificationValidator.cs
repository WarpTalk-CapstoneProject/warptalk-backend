using FluentValidation;
using System.Text.RegularExpressions;
using WarpTalk.NotificationService.Application.DTOs.AdminNotifications;
using WarpTalk.NotificationService.Domain.Constants;

namespace WarpTalk.NotificationService.API.Validators;

public class CreateAdminNotificationValidator : AbstractValidator<CreateAdminNotificationDto>
{
    public CreateAdminNotificationValidator()
    {
        // Title & Content
        RuleFor(x => x.Title)
            .NotEmpty()
            .MaximumLength(255);

        RuleFor(x => x.Content)
            .NotEmpty()
            .Must(content => !HasHtmlTags(content))
            .WithErrorCode(NotificationConstants.ErrorHtmlNotAllowed)
            .WithMessage("HTML tags are not allowed in notification content.");

        // Type
        RuleFor(x => x.Type)
            .NotEmpty()
            .Must(type => type == NotificationConstants.TypePromotion || 
                          type == NotificationConstants.TypeSystem || 
                          type == NotificationConstants.TypeAnnouncement || 
                          type == NotificationConstants.TypeMaintenance)
            .WithMessage("Invalid notification type.");

        // Target Audience Mode
        RuleFor(x => x.TargetAudienceMode)
            .NotEmpty()
            .Must(mode => mode == NotificationConstants.TargetModeBroadcast || 
                          mode == NotificationConstants.TargetModeSegment || 
                          mode == NotificationConstants.TargetModeSpecificUsers)
            .WithMessage("Invalid target audience mode.");

        When(x => x.TargetAudienceMode == NotificationConstants.TargetModeSpecificUsers, () =>
        {
            RuleFor(x => x.SpecificUserIds)
                .NotEmpty().WithMessage("SpecificUserIds must not be empty when mode is SPECIFIC_USERS.");
        });

        When(x => x.TargetAudienceMode == NotificationConstants.TargetModeSegment, () =>
        {
            RuleFor(x => x.SegmentId)
                .NotNull().WithMessage("SegmentId is required when mode is SEGMENT.");
        });

        // Type-specific rules: SYSTEM
        When(x => x.Type == NotificationConstants.TypeSystem, () =>
        {
            RuleFor(x => x.DiscountCode)
                .Empty().WithErrorCode(NotificationConstants.ErrorUnsupportedPayloadField);
            RuleFor(x => x.ImageUrl)
                .Empty().WithErrorCode(NotificationConstants.ErrorUnsupportedPayloadField);
            RuleFor(x => x.CtaLink)
                .Empty().WithErrorCode(NotificationConstants.ErrorUnsupportedPayloadField);
        });

        // Type-specific rules: MAINTENANCE
        When(x => x.Type == NotificationConstants.TypeMaintenance, () =>
        {
            RuleFor(x => x.DowntimeStart)
                .NotNull().WithMessage("DowntimeStart is required for maintenance notifications.");
            
            RuleFor(x => x.DowntimeEnd)
                .NotNull().WithMessage("DowntimeEnd is required for maintenance notifications.");

            RuleFor(x => x.DowntimeEnd)
                .GreaterThan(x => x.DowntimeStart)
                .When(x => x.DowntimeStart.HasValue && x.DowntimeEnd.HasValue)
                .WithMessage("DowntimeEnd must be strictly after DowntimeStart.");
        });
    }

    private static bool HasHtmlTags(string input)
    {
        if (string.IsNullOrEmpty(input)) return false;
        // Simple HTML tag detection
        return Regex.IsMatch(input, @"<[^>]+>");
    }
}

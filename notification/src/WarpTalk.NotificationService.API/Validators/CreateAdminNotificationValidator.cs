using FluentValidation;
using System.Text.RegularExpressions;
using WarpTalk.NotificationService.Application.DTOs.AdminNotifications;
using WarpTalk.NotificationService.Domain.Constants;

namespace WarpTalk.NotificationService.API.Validators;

public class CreateAdminNotificationValidator : AbstractValidator<CreateAdminNotificationDto>
{
    public CreateAdminNotificationValidator()
    {

        RuleFor(x => x.Title)
            .NotEmpty()
            .MaximumLength(255);

        RuleFor(x => x.Content)
            .NotEmpty()
            .Must(content => !HasHtmlTags(content))
            .WithErrorCode(NotificationConstants.ErrorHtmlNotAllowed)
            .WithMessage("HTML tags are not allowed in notification content.");


        RuleFor(x => x.Type)
            .NotEmpty()
            .Must(type => type == NotificationConstants.TypePromotion ||
                          type == NotificationConstants.TypeSystem ||
                          type == NotificationConstants.TypeAnnouncement ||
                          type == NotificationConstants.TypeMaintenance)
            .WithMessage("Invalid notification type.");


        // WT-699 / TC4104: all three modes the model defines. BROADCAST (every active account)
        // and SEGMENT (every active member of one workspace) are resolved to user ids by
        // IAdminAudienceResolver when the announcement is created.
        RuleFor(x => x.TargetAudienceMode)
            .NotEmpty()
            .Must(mode => mode == NotificationConstants.TargetModeSpecificUsers
                          || mode == NotificationConstants.TargetModeBroadcast
                          || mode == NotificationConstants.TargetModeSegment)
            .WithMessage("TargetAudienceMode must be BROADCAST, SEGMENT or SPECIFIC_USERS.");

        // A broadcast names nobody; a list alongside it would be ignored, so refuse it rather than
        // let the admin believe it narrowed anything.
        When(x => x.TargetAudienceMode == NotificationConstants.TargetModeBroadcast, () =>
        {
            RuleFor(x => x.SpecificUserIds)
                .Empty().WithMessage("SpecificUserIds must be empty when mode is BROADCAST.");
            RuleFor(x => x.SegmentId)
                .Null().WithMessage("SegmentId must be empty when mode is BROADCAST.");
        });

        When(x => x.TargetAudienceMode == NotificationConstants.TargetModeSpecificUsers, () =>
        {
            RuleFor(x => x.SpecificUserIds)
                .NotEmpty().WithMessage("SpecificUserIds must not be empty when mode is SPECIFIC_USERS.");
        });

        When(x => x.TargetAudienceMode == NotificationConstants.TargetModeSegment, () =>
        {
            RuleFor(x => x.SegmentId)
                .NotNull().WithMessage("SegmentId is required when mode is SEGMENT.")
                .NotEqual(Guid.Empty).WithMessage("SegmentId is required when mode is SEGMENT.");
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

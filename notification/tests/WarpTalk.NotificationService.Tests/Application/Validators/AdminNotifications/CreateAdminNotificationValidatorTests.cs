using System;
using System.Collections.Generic;
using FluentValidation.TestHelper;
using WarpTalk.NotificationService.Application.DTOs.AdminNotifications;
using WarpTalk.NotificationService.API.Validators;
using WarpTalk.NotificationService.Domain.Constants;
using Xunit;

namespace WarpTalk.NotificationService.Tests.Application.Validators.AdminNotifications;

public class CreateAdminNotificationValidatorTests
{
    private readonly CreateAdminNotificationValidator _validator;

    public CreateAdminNotificationValidatorTests()
    {
        _validator = new CreateAdminNotificationValidator();
    }

    private CreateAdminNotificationDto CreateValidBaseDto(string type = NotificationConstants.TypeSystem)
    {
        return new CreateAdminNotificationDto(
            Title: "Valid Title",
            Content: "Valid Content without HTML.",
            Type: type,
            TargetAudienceMode: NotificationConstants.TargetModeBroadcast,
            SpecificUserIds: null,
            SegmentId: null
        );
    }

    [Fact]
    public void Should_Have_Error_When_Title_Is_Empty()
    {
        var model = CreateValidBaseDto() with { Title = string.Empty };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void Should_Have_Error_When_Title_Exceeds_MaxLength()
    {
        var model = CreateValidBaseDto() with { Title = new string('A', 256) };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void Should_Have_Error_When_Content_Contains_Html()
    {
        var model = CreateValidBaseDto() with { Content = "This has <script>alert(1)</script> HTML" };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Content)
              .WithErrorCode(NotificationConstants.ErrorHtmlNotAllowed);
    }

    [Fact]
    public void Should_Have_Error_When_Type_Is_Invalid()
    {
        var model = CreateValidBaseDto(type: "UNKNOWN_TYPE");
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Type);
    }

    [Fact]
    public void Should_Have_Error_When_TargetAudience_Is_SpecificUsers_But_List_Is_Empty()
    {
        var model = CreateValidBaseDto() with 
        { 
            TargetAudienceMode = NotificationConstants.TargetModeSpecificUsers,
            SpecificUserIds = new List<Guid>() 
        };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.SpecificUserIds);
    }

    [Fact]
    public void Should_Have_Error_When_TargetAudience_Is_Segment_But_Id_Is_Null()
    {
        var model = CreateValidBaseDto() with 
        { 
            TargetAudienceMode = NotificationConstants.TargetModeSegment,
            SegmentId = null 
        };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.SegmentId);
    }

    [Fact]
    public void SystemType_Should_Reject_Promotional_Fields()
    {
        var model = CreateValidBaseDto(type: NotificationConstants.TypeSystem) with 
        { 
            DiscountCode = "SUMMER2026",
            Severity = "High"
        };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.DiscountCode)
              .WithErrorCode(NotificationConstants.ErrorUnsupportedPayloadField);
        result.ShouldNotHaveValidationErrorFor(x => x.Severity);
    }

    [Fact]
    public void MaintenanceType_Should_Require_Downtime_Fields()
    {
        var model = CreateValidBaseDto(type: NotificationConstants.TypeMaintenance);
        var result = _validator.TestValidate(model);
        
        // Missing start/end
        result.ShouldHaveValidationErrorFor(x => x.DowntimeStart);
        result.ShouldHaveValidationErrorFor(x => x.DowntimeEnd);
    }

    [Fact]
    public void MaintenanceType_Should_Reject_End_Before_Start()
    {
        var model = CreateValidBaseDto(type: NotificationConstants.TypeMaintenance) with 
        {
            DowntimeStart = DateTime.UtcNow.AddHours(2),
            DowntimeEnd = DateTime.UtcNow.AddHours(1)
        };
        var result = _validator.TestValidate(model);
        
        result.ShouldHaveValidationErrorFor(x => x.DowntimeEnd);
    }
}

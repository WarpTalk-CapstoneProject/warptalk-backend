using System.Text.Json;
using WarpTalk.NotificationService.API.Middlewares;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.NotificationService.Tests;

public class NotificationValidatorTests
{
    [Theory]
    [InlineData("Valid title", "Valid content")]
    [InlineData("123", "abc")]
    [InlineData("Title <", "Content >")] // Not a full tag
    public void Validate_ValidText_ReturnsSuccess(string title, string content)
    {
        var result = NotificationValidator.Validate(NotificationConstants.DefaultNotificationType, title, content, null, "{}");
        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("Title <b>Bold</b>", "Content")]
    [InlineData("Title", "Content <script>alert(1)</script>")]
    [InlineData("Title", "<img src='x' onerror='alert(1)'>")]
    public void Validate_HtmlInText_ReturnsHtmlNotAllowed(string title, string content)
    {
        var result = NotificationValidator.Validate(NotificationConstants.DefaultNotificationType, title, content, null, "{}");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Equal(NotificationConstants.ErrorHtmlNotAllowed, result.Error);
    }

    [Fact]
    public void Validate_HtmlInPayloadString_ReturnsHtmlNotAllowed()
    {
        var result = NotificationValidator.Validate(NotificationConstants.DefaultNotificationType, "Title", "Content", "http://example.com<script>", "{}");

        Assert.False(result.IsSuccess);
        Assert.Equal(NotificationConstants.ErrorHtmlNotAllowed, result.Error);
    }

    [Fact]
    public void Validate_UnknownPayloadKey_ReturnsUnsupportedField()
    {
        var payload = JsonSerializer.Serialize(new { action_url = "url", secret_key = "123" });

        var result = NotificationValidator.Validate(NotificationConstants.DefaultNotificationType, "Title", "Content", null, payload);

        Assert.False(result.IsSuccess);
        Assert.Equal(NotificationConstants.ErrorUnsupportedPayloadField, result.Error);
    }

    [Fact]
    public void Validate_InvalidFieldType_ReturnsInvalidFieldType()
    {
        // meeting_id should be string, passing number
        var payload = JsonSerializer.Serialize(new { meeting_id = 123, inviter_name = "Alice" });

        var result = NotificationValidator.Validate(NotificationConstants.TypeMeetingInvite, "Title", "Content", null, payload);

        Assert.False(result.IsSuccess);
        Assert.Equal(NotificationConstants.ErrorInvalidFieldType, result.Error);
    }

    [Fact]
    public void Validate_MissingRequiredFields_ReturnsMissingRequiredFields()
    {
        // missing inviter_name
        var payload = JsonSerializer.Serialize(new { meeting_id = "123" });

        var result = NotificationValidator.Validate(NotificationConstants.TypeMeetingInvite, "Title", "Content", null, payload);

        Assert.False(result.IsSuccess);
        Assert.Equal(NotificationConstants.ErrorMissingRequiredFields, result.Error);
    }

    [Fact]
    public void Validate_ValidPayload_ReturnsSuccess()
    {
        var payload = JsonSerializer.Serialize(new
        {
            meeting_id = "123",
            inviter_name = "Alice"
        });
        var result = NotificationValidator.Validate(NotificationConstants.TypeMeetingInvite, "Title", "Content", "http://localhost/meet", payload);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Validate_EmptyPayloadForRequiredType_ReturnsMissingRequiredFields()
    {
        var result = NotificationValidator.Validate(NotificationConstants.TypeMeetingInvite, "Title", "Content", null, "{}");

        Assert.False(result.IsSuccess);
        Assert.Equal(NotificationConstants.ErrorMissingRequiredFields, result.Error);
    }

    // The production defect of 2026-08-13: a user was invited to a meeting and "bị lỗi ở noti".
    // MEETING_STARTED and MEETING_SUMMARY_READY are published by translation-room and were
    // missing from the schema table, so an unknown type carrying a payload was rejected outright
    // — every "your meeting started" and every "your summary is ready" notification silently
    // discarded. Neither producer reads the reply, so the only trace was a log warning.
    // MEETING_INVITED is the third one, found the same way and left behind by the same fix: it is
    // the type translation-room sends for an invitation (past tense), while the constant that WAS
    // registered is "MEETING_INVITE" with a meeting_id/inviter_name schema that no producer emits.
    // So the bell rang for started and summary-ready meetings but never for being invited to one.
    [Theory]
    [InlineData(NotificationConstants.TypeMeetingStarted)]
    [InlineData(NotificationConstants.TypeMeetingSummaryReady)]
    [InlineData(NotificationConstants.TypeMeetingInvited)]
    public void Validate_MeetingLifecyclePayload_IsAccepted(string type)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            room_id = "019ff9e1-e3e2-7024-99b7-6e37c6a18392",
            room_title = "Test 13/8"
        });

        var result = NotificationValidator.Validate(type, "Title", "Content", "http://localhost/room", payload);

        Assert.True(result.IsSuccess);
    }

    // Its own test rather than another InlineData above: this type carries four fields, and the
    // lifecycle payload carries two. Registered here in the same commit as its producer — the
    // fifth type to be added, and the first not to spend a while being silently discarded.
    [Fact]
    public void Validate_ActionItemAssignedPayload_IsAccepted()
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            room_id = "019ff9e1-e3e2-7024-99b7-6e37c6a18392",
            room_title = "Sprint review",
            action_item_id = "019ff9e1-e3e2-7024-99b7-6e37c6a18393",
            task = "Viết release note"
        });

        var result = NotificationValidator.Validate(
            NotificationConstants.TypeActionItemAssigned,
            "Title", "Content", "http://localhost/room", payload);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Validate_ActionItemAssignedWithoutItsTask_IsRejected()
    {
        // A "you were given a task" notification with no task on it has nothing to show.
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            room_id = "019ff9e1-e3e2-7024-99b7-6e37c6a18392",
            room_title = "Sprint review",
            action_item_id = "019ff9e1-e3e2-7024-99b7-6e37c6a18393"
        });

        var result = NotificationValidator.Validate(
            NotificationConstants.TypeActionItemAssigned,
            "Title", "Content", "http://localhost/room", payload);

        Assert.False(result.IsSuccess);
    }

    [Theory]
    [InlineData(NotificationConstants.TypeMeetingStarted)]
    [InlineData(NotificationConstants.TypeMeetingSummaryReady)]
    [InlineData(NotificationConstants.TypeMeetingInvited)]
    public void Validate_MeetingLifecycleWithoutItsRoom_IsRejected(string type)
    {
        // Registered as REQUIRED on purpose: a notification about a meeting, with no meeting on
        // it, has nothing for the client to open.
        var result = NotificationValidator.Validate(type, "Title", "Content", null, "{}");

        Assert.False(result.IsSuccess);
        Assert.Equal(NotificationConstants.ErrorMissingRequiredFields, result.Error);
    }
}

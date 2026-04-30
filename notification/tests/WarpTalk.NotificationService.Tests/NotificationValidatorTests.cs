using System.Text.Json;
using WarpTalk.NotificationService.Application.Validators;
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
        var result = NotificationValidator.Validate("SYSTEM", title, content, "{}");
        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("Title <b>Bold</b>", "Content")]
    [InlineData("Title", "Content <script>alert(1)</script>")]
    [InlineData("Title", "<img src='x' onerror='alert(1)'>")]
    public void Validate_HtmlInText_ReturnsHtmlNotAllowed(string title, string content)
    {
        var result = NotificationValidator.Validate("SYSTEM", title, content, "{}");
        
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Equal("HTML_NOT_ALLOWED", result.Error);
    }

    [Fact]
    public void Validate_HtmlInPayloadString_ReturnsHtmlNotAllowed()
    {
        var payload = JsonSerializer.Serialize(new { action_url = "http://example.com<script>" });
        
        var result = NotificationValidator.Validate("SYSTEM", "Title", "Content", payload);
        
        Assert.False(result.IsSuccess);
        Assert.Equal("HTML_NOT_ALLOWED", result.Error);
    }

    [Fact]
    public void Validate_UnknownPayloadKey_ReturnsUnsupportedField()
    {
        var payload = JsonSerializer.Serialize(new { action_url = "url", secret_key = "123" });
        
        var result = NotificationValidator.Validate("SYSTEM", "Title", "Content", payload);
        
        Assert.False(result.IsSuccess);
        Assert.Equal("UNSUPPORTED_PAYLOAD_FIELD", result.Error);
    }

    [Fact]
    public void Validate_InvalidFieldType_ReturnsInvalidFieldType()
    {
        // meeting_id should be string, passing number
        var payload = JsonSerializer.Serialize(new { meeting_id = 123, inviter_name = "Alice" });
        
        var result = NotificationValidator.Validate("MEETING_INVITE", "Title", "Content", payload);
        
        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_FIELD_TYPE", result.Error);
    }

    [Fact]
    public void Validate_MissingRequiredFields_ReturnsMissingRequiredFields()
    {
        // missing inviter_name
        var payload = JsonSerializer.Serialize(new { meeting_id = "123" });
        
        var result = NotificationValidator.Validate("MEETING_INVITE", "Title", "Content", payload);
        
        Assert.False(result.IsSuccess);
        Assert.Equal("MISSING_REQUIRED_FIELDS", result.Error);
    }

    [Fact]
    public void Validate_ValidPayload_ReturnsSuccess()
    {
        var payload = JsonSerializer.Serialize(new 
        { 
            meeting_id = "123", 
            inviter_name = "Alice",
            action_url = "http://localhost/meet"
        });
        
        var result = NotificationValidator.Validate("MEETING_INVITE", "Title", "Content", payload);
        
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Validate_EmptyPayloadForRequiredType_ReturnsMissingRequiredFields()
    {
        var result = NotificationValidator.Validate("MEETING_INVITE", "Title", "Content", "{}");
        
        Assert.False(result.IsSuccess);
        Assert.Equal("MISSING_REQUIRED_FIELDS", result.Error);
    }
}

using WarpTalk.NotificationService.Application.Services;

namespace WarpTalk.NotificationService.Tests.Application.Services;

public sealed class EmailTemplateRendererTests
{
    [Fact]
    public void RenderGenericNotification_EncodesContentAndRejectsUnsafeActionUrl()
    {
        var html = EmailTemplateRenderer.RenderGenericNotification(
            "<script>alert('title')</script>",
            "Hello <img src=x onerror=alert(1)>",
            "javascript:alert(1)");

        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://warptalk.app", html);
    }
}

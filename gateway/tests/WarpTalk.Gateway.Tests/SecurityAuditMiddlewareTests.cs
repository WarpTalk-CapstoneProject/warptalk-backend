using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using WarpTalk.Gateway.Middleware;

namespace WarpTalk.Gateway.Tests;

public sealed class SecurityAuditMiddlewareTests
{
    [Fact]
    public async Task SensitiveRequestEmitsAttributableAuditEventWithoutPayload()
    {
        var logger = new CapturingLogger<SecurityAuditMiddleware>();
        var middleware = new SecurityAuditMiddleware(
            async context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                await Task.CompletedTask;
            },
            logger);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "admin-123")],
                authenticationType: "test"))
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/billing/credits/adjust";
        context.Request.Headers["X-Workspace-Id"] = Guid.NewGuid().ToString();

        await middleware.InvokeAsync(context);

        var audit = Assert.Single(logger.Entries);
        Assert.Contains("security_audit", audit, StringComparison.Ordinal);
        Assert.Contains("admin-123", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("request_body", audit, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(formatter(state, exception));
    }
}

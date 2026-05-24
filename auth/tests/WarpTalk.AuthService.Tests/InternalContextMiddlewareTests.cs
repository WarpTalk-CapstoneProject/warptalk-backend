using System;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using WarpTalk.Shared.Middleware;
using Xunit;

namespace WarpTalk.AuthService.Tests;

public class InternalContextMiddlewareTests
{
    private readonly RequestDelegate _next;
    private readonly InternalContextMiddleware _middleware;
    private readonly string _sharedSecret = "super-secret-cluster-shared-key-for-jwt-signing-123456";

    public InternalContextMiddlewareTests()
    {
        _next = Substitute.For<RequestDelegate>();
        _middleware = new InternalContextMiddleware(_next, _sharedSecret);
    }

    [Fact]
    public async Task InvokeAsync_ShouldBindContext_WhenValidSignedHeaderProvided()
    {
        // Arrange
        var context = new DefaultHttpContext();
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        
        // Generate a valid signed token
        var token = TokenGeneratorHelper.GenerateInternalSignedToken(userId, workspaceId, _sharedSecret);
        context.Request.Headers["X-Internal-Context"] = token;

        var workspaceContext = Substitute.For<IWorkspaceContext>();
        context.RequestServices = Substitute.For<IServiceProvider>();
        context.RequestServices.GetService(typeof(IWorkspaceContext)).Returns(workspaceContext);

        // Act
        await _middleware.InvokeAsync(context);

        // Assert
        await _next.Received(1).Invoke(context);
        workspaceContext.Received(1).SetContext(userId, workspaceId);
    }

    [Fact]
    public async Task InvokeAsync_ShouldNotBindContext_AndReturnUnauthorized_WhenInvalidSignatureProvided()
    {
        // Arrange
        var context = new DefaultHttpContext();
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        
        // Generate token with a different secret
        var token = TokenGeneratorHelper.GenerateInternalSignedToken(userId, workspaceId, "wrong-secret-key-12345678901234567890");
        context.Request.Headers["X-Internal-Context"] = token;

        context.Response.Body = new MemoryStream();

        // Act
        await _middleware.InvokeAsync(context);

        // Assert
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        await _next.DidNotReceive().Invoke(Arg.Any<HttpContext>());
    }

    [Fact]
    public async Task InvokeAsync_ShouldPassRequest_WithoutContext_WhenNoHeaderProvided()
    {
        // Arrange
        var context = new DefaultHttpContext();
        var workspaceContext = Substitute.For<IWorkspaceContext>();
        context.RequestServices = Substitute.For<IServiceProvider>();
        context.RequestServices.GetService(typeof(IWorkspaceContext)).Returns(workspaceContext);

        // Act
        await _middleware.InvokeAsync(context);

        // Assert
        await _next.Received(1).Invoke(context);
        workspaceContext.DidNotReceive().SetContext(Arg.Any<Guid>(), Arg.Any<Guid>());
    }
}

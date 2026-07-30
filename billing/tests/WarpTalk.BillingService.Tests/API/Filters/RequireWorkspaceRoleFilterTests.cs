using System.Security.Claims;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.BillingService.API.Filters;
using WarpTalk.Shared.Protos;

namespace WarpTalk.BillingService.Tests.API.Filters;

public class RequireWorkspaceRoleFilterTests
{
    [Fact]
    public void AddBillingAuthorizationDependencies_RegistersMemoryCache()
    {
        var services = new ServiceCollection();

        services.AddBillingAuthorizationDependencies();

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<IMemoryCache>());
    }

    [Fact]
    public async Task SameUserAndWorkspaceId_StillRequiresActiveWorkspaceMembership()
    {
        var userId = Guid.NewGuid();
        var client = new WorkspaceService.WorkspaceServiceClient(
            new WorkspaceMemberCallInvoker(new GetWorkspaceMemberResponse
            {
                IsMember = false,
                IsActive = false
            }));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var filter = new RequireWorkspaceRoleFilter(client, cache, ["Owner", "Admin"]);
        var (context, wasNextCalled) = CreateContext(userId, userId);

        await filter.OnActionExecutionAsync(context, wasNextCalled.Next);

        var result = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.False(wasNextCalled.Value);
    }

    [Fact]
    public async Task MissingWorkspaceId_FailsClosedWithoutInvokingAction()
    {
        var userId = Guid.NewGuid();
        var client = new WorkspaceService.WorkspaceServiceClient(new ThrowingCallInvoker());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var filter = new RequireWorkspaceRoleFilter(client, cache, ["Owner", "Admin"]);
        var (context, wasNextCalled) = CreateContext(userId, null);

        await filter.OnActionExecutionAsync(context, wasNextCalled.Next);

        var result = Assert.IsType<BadRequestObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.False(wasNextCalled.Value);
    }

    private static (ActionExecutingContext Context, NextTracker Tracker) CreateContext(
        Guid userId,
        Guid? workspaceId)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                "test"))
        };
        var routeData = new RouteData();
        if (workspaceId.HasValue)
        {
            routeData.Values["workspaceId"] = workspaceId.Value.ToString();
        }

        var actionContext = new ActionContext(
            httpContext,
            routeData,
            new ActionDescriptor(),
            new ModelStateDictionary());
        var tracker = new NextTracker(actionContext);
        var context = new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?>(),
            new object());
        return (context, tracker);
    }

    private sealed class NextTracker(ActionContext actionContext)
    {
        public bool Value { get; private set; }

        public Task<ActionExecutedContext> Next()
        {
            Value = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, [], new object()));
        }
    }

    private sealed class WorkspaceMemberCallInvoker(GetWorkspaceMemberResponse response) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            if (response is not TResponse typedResponse)
            {
                throw new InvalidOperationException($"Unexpected response type {typeof(TResponse).Name}.");
            }

            return new AsyncUnaryCall<TResponse>(
                Task.FromResult(typedResponse),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) => throw new NotSupportedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) => throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options) => throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options) => throw new NotSupportedException();
    }

    private sealed class ThrowingCallInvoker : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) => throw new InvalidOperationException("Workspace RPC must not be called.");

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) => throw new NotSupportedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) => throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options) => throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options) => throw new NotSupportedException();
    }
}

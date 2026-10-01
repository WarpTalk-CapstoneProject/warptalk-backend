using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.Shared;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.Shared.Events;
using Xunit;

namespace WarpTalk.BillingService.Tests.Infrastructure;

/// <summary>A sink that remembers what it was given and can be told to refuse.</summary>
internal sealed class RecordingAuditSink : IAdminAuditSink
{
    public List<AdminAuditRecord> Records { get; } = [];
    public bool Refuse { get; set; }

    public Task<bool> RecordAsync(AdminAuditRecord record, CancellationToken ct = default)
    {
        if (Refuse) return Task.FromResult(false);
        Records.Add(record);
        return Task.FromResult(true);
    }
}

/// <summary>
/// The save-time half of [AdminAudited]: what it records from the change tracker, and that a
/// refusal stops the save. No database is needed for either — the diff is read before any SQL.
/// </summary>
public class AdminAuditInterceptorTests
{
    private static readonly Guid Actor = Guid.NewGuid();

    private static BillingDbContext Context() =>
        new(new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options);

    private static AdminAuditInvocation Invocation(string action, string entityType, params Type[] subjects) => new()
    {
        Action = action,
        EntityType = entityType,
        SubjectTypes = subjects,
        ActorId = Actor,
        CorrelationId = "trace-1",
        Reason = "Quarterly repricing",
        Metadata = new AdminAuditRequestMetadata("root@warptalk.io.vn", null, "203.0.113.7", "Mozilla/5.0"),
    };

    private static Plan NewPlan() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Pro",
        Slug = "pro",
        Tier = "pro",
        Price = 499000m,
        CreditsPerCycle = 50000,
        MaxParticipants = 10,
        UpdatedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task A_modified_plan_is_recorded_with_only_the_fields_that_changed()
    {
        using var context = Context();
        var plan = NewPlan();
        context.Attach(plan);
        plan.Price = 599000m;
        plan.MaxParticipants = 25;
        plan.UpdatedAt = DateTime.UtcNow;
        var sink = new RecordingAuditSink();
        var invocation = Invocation(AdminAuditBillingActions.PlanUpdated, AdminAuditEntityTypes.Plan, typeof(Plan));

        await AdminAuditSaveChangesInterceptor.RecordPendingAsync(context, invocation, sink, CancellationToken.None);

        var record = Assert.Single(sink.Records);
        Assert.Equal(AdminAuditBillingActions.PlanUpdated, record.Action);
        Assert.Equal(plan.Id, record.EntityId);
        Assert.Equal("Pro", record.EntityLabel);
        Assert.Equal(Actor, record.ActorId);
        Assert.Equal("Quarterly repricing", record.Reason);
        Assert.Equal("trace-1", record.CorrelationId);
        Assert.Equal("203.0.113.7", record.Metadata.IpAddress);
        Assert.Equal(new Dictionary<string, string?> { ["price"] = "499000", ["max_participants"] = "10" }, record.BeforeSummary);
        Assert.Equal(new Dictionary<string, string?> { ["price"] = "599000", ["max_participants"] = "25" }, record.AfterSummary);
        Assert.True(invocation.HasRecorded);
    }

    [Fact]
    public async Task A_decimal_that_only_changed_scale_is_not_a_change()
    {
        using var context = Context();
        var plan = NewPlan();
        plan.Price = 499000.00m;
        context.Attach(plan);
        plan.Price = 499000m;
        plan.MaxParticipants = 11;
        var sink = new RecordingAuditSink();

        await AdminAuditSaveChangesInterceptor.RecordPendingAsync(
            context, Invocation(AdminAuditBillingActions.PlanUpdated, AdminAuditEntityTypes.Plan, typeof(Plan)), sink, CancellationToken.None);

        var record = Assert.Single(sink.Records);
        Assert.False(record.AfterSummary!.ContainsKey("price"));
        Assert.Equal("0.012", AdminAuditSaveChangesInterceptor.Format(0.01200m));
    }

    [Fact]
    public async Task A_new_row_is_named_by_the_id_it_will_be_inserted_with()
    {
        using var context = Context();
        var plan = NewPlan();
        plan.Id = Guid.Empty;
        context.Plans.Add(plan);
        var sink = new RecordingAuditSink();

        await AdminAuditSaveChangesInterceptor.RecordPendingAsync(
            context, Invocation(AdminAuditBillingActions.PlanCreated, AdminAuditEntityTypes.Plan, typeof(Plan)), sink, CancellationToken.None);

        var record = Assert.Single(sink.Records);
        Assert.NotEqual(Guid.Empty, plan.Id);
        Assert.Equal(plan.Id, record.EntityId);
        Assert.Equal(7, plan.Id.Version);
        Assert.Null(record.BeforeSummary);
        Assert.Equal("Pro", record.AfterSummary!["name"]);
    }

    [Fact]
    public async Task Rows_keyed_by_a_string_carry_the_key_and_do_not_collide_in_the_store()
    {
        using var context = Context();
        var markup = new BillingPricingConfig { Key = "markup_percent", Value = 30m, UpdatedAt = DateTime.UtcNow };
        var fx = new BillingPricingConfig { Key = "usd_vnd_rate", Value = 25000m, UpdatedAt = DateTime.UtcNow };
        context.Attach(markup);
        context.Attach(fx);
        markup.Value = 35m;
        fx.Value = 26000m;
        var sink = new RecordingAuditSink();

        await AdminAuditSaveChangesInterceptor.RecordPendingAsync(
            context,
            Invocation(AdminAuditBillingActions.PricingConfigUpdated, AdminAuditEntityTypes.PricingConfig, typeof(BillingPricingConfig)),
            sink,
            CancellationToken.None);

        Assert.Equal(2, sink.Records.Count);
        Assert.All(sink.Records, record => Assert.Null(record.EntityId));
        Assert.Equal(new[] { "markup_percent", "usd_vnd_rate" }, sink.Records.Select(r => r.EntityKey!).Order().ToArray());
        Assert.Equal(2, sink.Records.Select(r => r.CorrelationId).Distinct().Count());
        Assert.Contains(sink.Records, r => r.CorrelationId == "trace-1#markup_percent");
    }

    [Fact]
    public async Task Rows_that_are_not_the_subject_are_not_recorded()
    {
        using var context = Context();
        context.Add(new OutboxMessage { Id = Guid.NewGuid(), EventType = "billing.entitlements_changed" });
        var sink = new RecordingAuditSink();
        var invocation = Invocation(AdminAuditBillingActions.PlanUpdated, AdminAuditEntityTypes.Plan, typeof(Plan));

        await AdminAuditSaveChangesInterceptor.RecordPendingAsync(context, invocation, sink, CancellationToken.None);

        Assert.Empty(sink.Records);
        Assert.False(invocation.HasRecorded);
    }

    [Fact]
    public async Task A_refused_record_stops_the_save()
    {
        using var context = Context();
        var plan = NewPlan();
        context.Attach(plan);
        plan.Price = 1m;
        var sink = new RecordingAuditSink { Refuse = true };
        var invocation = Invocation(AdminAuditBillingActions.PlanUpdated, AdminAuditEntityTypes.Plan, typeof(Plan));

        await Assert.ThrowsAsync<AdminAuditRefusedException>(() =>
            AdminAuditSaveChangesInterceptor.RecordPendingAsync(context, invocation, sink, CancellationToken.None));
        Assert.False(invocation.HasRecorded);
    }

    [Fact]
    public async Task The_interceptor_does_nothing_on_a_save_no_request_armed()
    {
        var accessor = new HttpContextAccessor { HttpContext = null };
        using var context = new BillingDbContext(new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .AddInterceptors(new AdminAuditSaveChangesInterceptor(accessor))
            .Options);
        var plan = NewPlan();
        context.Attach(plan);
        plan.Price = 1m;

        // No scope, so the interceptor must not throw or record; the save then fails only because
        // there is no database, which is the part this test does not care about.
        var error = await Record.ExceptionAsync(() => context.SaveChangesAsync());
        Assert.IsNotType<AdminAuditRefusedException>(error);
    }
}

/// <summary>The request half of [AdminAudited]: who is armed, and what an error is recorded as.</summary>
public class AdminAuditActionFilterTests
{
    private static readonly Guid Actor = Guid.NewGuid();

    private static ClaimsPrincipal User(params string[] roles) => new(new ClaimsIdentity(
        new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Actor.ToString()),
            new Claim(ClaimTypes.Email, "root@warptalk.io.vn"),
        }.Concat(roles.Select(role => new Claim(ClaimTypes.Role, role))),
        "test"));

    private static (ActionExecutingContext Context, DefaultHttpContext Http) Executing(
        ClaimsPrincipal user, AdminAuditedAttribute? attribute, IDictionary<string, object?> arguments, RouteValueDictionary route)
    {
        var http = new DefaultHttpContext { User = user };
        http.Request.Headers["X-Forwarded-For"] = "198.51.100.4";
        var descriptor = new ActionDescriptor { EndpointMetadata = attribute is null ? [] : [attribute] };
        var routeData = new RouteData(route);
        var actionContext = new ActionContext(http, routeData, descriptor);
        return (new ActionExecutingContext(actionContext, [], arguments, controller: new object()), http);
    }

    private static ActionExecutionDelegate Returns(IActionResult result, ActionExecutingContext context) => () =>
        Task.FromResult(new ActionExecutedContext(context, [], controller: new object()) { Result = result });

    private sealed record ReasonBody(string Reason);

    [Fact]
    public async Task A_refused_call_is_recorded_as_failed_with_the_error_the_admin_saw()
    {
        var sink = new RecordingAuditSink();
        var scope = new AdminAuditScope();
        var filter = new AdminAuditActionFilter(scope, sink, NullLogger<AdminAuditActionFilter>.Instance);
        var planId = Guid.NewGuid();
        var (context, _) = Executing(
            User("admin"),
            new AdminAuditedAttribute(AdminAuditBillingActions.PlanUpdated, AdminAuditEntityTypes.Plan, typeof(Plan)) { EntityRouteKey = "id" },
            new Dictionary<string, object?> { ["request"] = new ReasonBody("Price correction") },
            new RouteValueDictionary { ["id"] = planId.ToString() });

        await filter.OnActionExecutionAsync(context, Returns(
            new BadRequestObjectResult(new ApiErrorResponse("Plan name already exists.", ErrorCodes.ValidationError)), context));

        var record = Assert.Single(sink.Records);
        Assert.Equal(AdminAuditResults.Failed, record.Result);
        Assert.Equal("Plan name already exists.", record.ErrorMessage);
        Assert.Equal(planId, record.EntityId);
        Assert.Equal("Price correction", record.Reason);
        Assert.EndsWith(AdminAuditActionFilter.FailedSuffix, record.CorrelationId);
        Assert.Equal("198.51.100.4", record.Metadata.IpAddress);
        Assert.Equal("root@warptalk.io.vn", record.Metadata.ActorEmail);
    }

    [Fact]
    public async Task A_failure_after_a_recorded_save_supersedes_each_recorded_attempt()
    {
        var sink = new RecordingAuditSink();
        var scope = new AdminAuditScope();
        var filter = new AdminAuditActionFilter(scope, sink, NullLogger<AdminAuditActionFilter>.Instance);
        var (context, _) = Executing(
            User("admin"),
            new AdminAuditedAttribute(AdminAuditBillingActions.RateCardUpserted, AdminAuditEntityTypes.UsageRate, typeof(UsageRateCard)),
            new Dictionary<string, object?>(),
            new RouteValueDictionary());
        var cardId = Guid.NewGuid();

        await filter.OnActionExecutionAsync(context, async () =>
        {
            // What the interceptor does on the first save; the request then fails.
            await sink.RecordAsync(new AdminAuditRecord
            {
                Action = AdminAuditBillingActions.RateCardUpserted,
                EntityType = AdminAuditEntityTypes.UsageRate,
                EntityId = cardId,
                ActorId = Actor,
                CorrelationId = scope.Current!.CorrelationId,
            });
            scope.Current!.AddRecorded([sink.Records[0]]);
            return new ActionExecutedContext(context, [], new object())
            {
                Result = new ObjectResult(new ApiErrorResponse("Could not commit.", ErrorCodes.InternalServerError)) { StatusCode = 500 },
            };
        });

        Assert.Equal(2, sink.Records.Count);
        var failed = sink.Records[1];
        Assert.Equal(AdminAuditResults.Failed, failed.Result);
        Assert.Equal(cardId, failed.EntityId);
        Assert.Equal(sink.Records[0].CorrelationId + AdminAuditActionFilter.FailedSuffix, failed.CorrelationId);
        Assert.Equal("Could not commit.", failed.ErrorMessage);
    }

    [Fact]
    public async Task A_successful_call_that_saved_nothing_is_still_recorded_once()
    {
        var sink = new RecordingAuditSink();
        var filter = new AdminAuditActionFilter(new AdminAuditScope(), sink, NullLogger<AdminAuditActionFilter>.Instance);
        var invoiceId = Guid.NewGuid();
        var (context, _) = Executing(
            User("admin"),
            new AdminAuditedAttribute(AdminAuditWorkspaceActions.InvoiceMarkedPaid, AdminAuditEntityTypes.Invoice, typeof(Invoice)) { EntityRouteKey = "invoiceId" },
            new Dictionary<string, object?>(),
            new RouteValueDictionary { ["invoiceId"] = invoiceId.ToString() });

        await filter.OnActionExecutionAsync(context, Returns(new OkObjectResult(new { }), context));

        var record = Assert.Single(sink.Records);
        Assert.Equal(AdminAuditResults.Succeeded, record.Result);
        Assert.Equal(invoiceId, record.EntityId);
        Assert.Null(record.Reason);
    }

    [Fact]
    public async Task A_workspace_owner_on_a_shared_route_is_not_a_platform_admin_action()
    {
        var sink = new RecordingAuditSink();
        var scope = new AdminAuditScope();
        var filter = new AdminAuditActionFilter(scope, sink, NullLogger<AdminAuditActionFilter>.Instance);
        var (context, _) = Executing(
            User(),
            new AdminAuditedAttribute(AdminAuditBillingActions.SubscriptionCancelled, AdminAuditEntityTypes.Subscription, typeof(Subscription)),
            new Dictionary<string, object?> { ["reason"] = "Too expensive" },
            new RouteValueDictionary { ["workspaceId"] = Guid.NewGuid().ToString() });

        await filter.OnActionExecutionAsync(context, Returns(new NoContentResult(), context));

        Assert.Null(scope.Current);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task A_query_string_reason_and_the_route_workspace_are_carried()
    {
        var sink = new RecordingAuditSink();
        var filter = new AdminAuditActionFilter(new AdminAuditScope(), sink, NullLogger<AdminAuditActionFilter>.Instance);
        var workspaceId = Guid.NewGuid();
        var (context, _) = Executing(
            User("admin"),
            new AdminAuditedAttribute(AdminAuditBillingActions.SubscriptionCancelled, AdminAuditEntityTypes.Subscription, typeof(Subscription)),
            new Dictionary<string, object?> { ["workspaceId"] = workspaceId, ["reason"] = "Chargeback" },
            new RouteValueDictionary { ["workspaceId"] = workspaceId.ToString() });

        await filter.OnActionExecutionAsync(context, Returns(new NoContentResult(), context));

        var record = Assert.Single(sink.Records);
        Assert.Equal("Chargeback", record.Reason);
        Assert.Equal(workspaceId, record.WorkspaceId);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using WarpTalk.BillingService.API.Authorization;
using WarpTalk.BillingService.API.Controllers;
using WarpTalk.Shared;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;
using Xunit;

namespace WarpTalk.BillingService.Tests.Architecture;

/// <summary>
/// Every write a platform administrator can make through the billing service lands in the platform
/// audit log.
///
/// Before this, only the admin workspace page's seven actions did (by hand, through
/// IAdminAuditRecorder). Plans, rate cards, pricing, VAT, contracts, the /admin/subscriptions
/// lifecycle buttons, the old change-plan route, the old mark-paid route, recorded payments and
/// sales-lead status all changed money-bearing rows with no entry anywhere — the audit screen
/// "looked hardcoded" because it only ever showed suspensions. This file is what notices the next
/// admin route added without [AdminAudited].
/// </summary>
public class AdminWriteAuditCoverageTests
{
    /// <summary>Writes audited by hand rather than by attribute; each has its own tests.</summary>
    private static readonly HashSet<Type> HandAuditedControllers = [typeof(AdminWorkspaceBillingController)];

    /// <summary>POSTs that change nothing.</summary>
    private static readonly HashSet<string> ReadOnlyPosts =
    [
        $"{nameof(UsagesController)}.{nameof(UsagesController.PreviewUsageRateCard)}",
    ];

    /// <summary>
    /// Routes shared with workspace owners that the admin portal also calls. The filter only
    /// records them when a platform administrator is the caller.
    /// </summary>
    public static TheoryData<Type, string, string> SharedRoutesTheAdminPortalCalls => new()
    {
        { typeof(SubscriptionsController), nameof(SubscriptionsController.CancelSubscription), AdminAuditBillingActions.SubscriptionCancelled },
        { typeof(SubscriptionsController), nameof(SubscriptionsController.ReactivateSubscription), AdminAuditBillingActions.SubscriptionReactivated },
        { typeof(SubscriptionsController), nameof(SubscriptionsController.ResumeSubscription), AdminAuditBillingActions.SubscriptionResumed },
    };

    private static IEnumerable<(Type Controller, MethodInfo Action)> Writes() =>
        typeof(AdminSubscriptionsController).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes<HttpMethodAttribute>()
                    .Any(http => http.HttpMethods.Any(verb => verb is "POST" or "PUT" or "PATCH" or "DELETE")))
                .Select(method => (type, method)));

    private static bool IsAdminOnly(Type controller, MethodInfo action)
    {
        var authorizations = action.GetCustomAttributes<AuthorizeAttribute>()
            .Concat(controller.GetCustomAttributes<AuthorizeAttribute>());
        // G10: an admin-only endpoint is one behind a staff permission.
        return authorizations.Any(authorize => authorize is RequirePermissionAttribute);
    }

    [Fact]
    public void Every_admin_only_write_is_audited()
    {
        // Guards against a vacuous pass: if IsAdminOnly stopped recognising the gate, nothing would
        // count as admin-only and every write would "be audited".
        Assert.Contains(Writes(), write => IsAdminOnly(write.Controller, write.Action));

        var unaudited = Writes()
            .Where(write => IsAdminOnly(write.Controller, write.Action))
            .Where(write => !HandAuditedControllers.Contains(write.Controller))
            .Where(write => !ReadOnlyPosts.Contains($"{write.Controller.Name}.{write.Action.Name}"))
            .Where(write => write.Action.GetCustomAttribute<AdminAuditedAttribute>() is null)
            .Select(write => $"{write.Controller.Name}.{write.Action.Name}")
            .ToList();

        Assert.True(unaudited.Count == 0, "Admin-only writes with no audit: " + string.Join(", ", unaudited));
    }

    [Theory]
    [MemberData(nameof(SharedRoutesTheAdminPortalCalls))]
    public void Shared_routes_the_admin_portal_calls_are_audited(Type controller, string actionName, string expectedAction)
    {
        var attribute = controller.GetMethod(actionName)!.GetCustomAttribute<AdminAuditedAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(expectedAction, attribute!.Action);
        Assert.NotNull(controller.GetMethod(actionName)!.GetCustomAttribute<RequireWorkspaceRoleAttribute>());
    }

    [Fact]
    public void The_old_change_plan_and_mark_paid_routes_are_audited_under_the_same_verbs_as_their_new_doors()
    {
        var changePlan = typeof(AdminSubscriptionsController).GetMethod(nameof(AdminSubscriptionsController.ChangePlan))!
            .GetCustomAttribute<AdminAuditedAttribute>();
        var markPaid = typeof(InvoicesController).GetMethod(nameof(InvoicesController.MarkInvoicePaid))!
            .GetCustomAttribute<AdminAuditedAttribute>();

        Assert.Equal(AdminAuditWorkspaceActions.PlanChanged, changePlan!.Action);
        Assert.Equal(AdminAuditEntityTypes.Subscription, changePlan.EntityType);
        Assert.Equal(AdminAuditWorkspaceActions.InvoiceMarkedPaid, markPaid!.Action);
        Assert.Equal("invoiceId", markPaid.EntityRouteKey);
    }

    [Fact]
    public void Every_audited_route_names_a_known_entity_a_subject_and_a_verb_that_fits_the_column()
    {
        var audited = Writes()
            .Select(write => (write, attribute: write.Action.GetCustomAttribute<AdminAuditedAttribute>()))
            .Where(pair => pair.attribute is not null)
            .ToList();

        Assert.NotEmpty(audited);
        foreach (var (write, attribute) in audited)
        {
            var name = $"{write.Controller.Name}.{write.Action.Name}";
            Assert.True(attribute!.Action.Length <= AdminAuditWorkspaceActions.MaxLength, $"{name}: '{attribute.Action}' is too long");
            Assert.Contains(attribute.EntityType, AdminAuditEntityTypes.All);
            Assert.NotEmpty(attribute.SubjectTypes);
        }
    }
}

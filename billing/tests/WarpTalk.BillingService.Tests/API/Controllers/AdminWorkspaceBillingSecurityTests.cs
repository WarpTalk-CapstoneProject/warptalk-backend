using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using WarpTalk.BillingService.API.Controllers;
using WarpTalk.Shared.Authorization;
using Xunit;

namespace WarpTalk.BillingService.Tests.API.Controllers;

/// <summary>
/// The admin workspace page's billing actions must be system-admin gated SERVER-side, by the
/// platform policy — never by <c>Roles = "Admin, admin"</c>, which admits the global Admin row
/// that seeded demo accounts carry.
/// </summary>
public class AdminWorkspaceBillingSecurityTests
{
    [Fact]
    public void The_controller_is_gated_by_the_system_admin_policy_and_by_no_role_string()
    {
        var authorize = typeof(AdminWorkspaceBillingController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal(SystemAdminAuthorization.PolicyName, authorize!.Policy);
        Assert.True(string.IsNullOrEmpty(authorize.Roles));
    }

    [Fact]
    public void No_action_loosens_the_gate()
    {
        var actions = typeof(AdminWorkspaceBillingController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
            .ToList();

        Assert.Equal(7, actions.Count);
        foreach (var action in actions)
        {
            Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>());
            var own = action.GetCustomAttribute<AuthorizeAttribute>();
            Assert.True(own is null || string.IsNullOrEmpty(own.Roles), $"{action.Name} must not authorize off a role string");
        }
    }

    [Theory]
    [InlineData(nameof(AdminWorkspaceBillingController.AdjustCredits), "credits/adjust")]
    [InlineData(nameof(AdminWorkspaceBillingController.ChangePlan), "subscription/change-plan")]
    [InlineData(nameof(AdminWorkspaceBillingController.ExtendTrial), "subscription/extend-trial")]
    [InlineData(nameof(AdminWorkspaceBillingController.CompPeriod), "subscription/comp")]
    [InlineData(nameof(AdminWorkspaceBillingController.SetEntitlementOverrides), "subscription/entitlements")]
    [InlineData(nameof(AdminWorkspaceBillingController.MarkInvoicePaid), "invoices/{invoiceId:guid}/mark-paid")]
    public void Every_write_has_the_route_the_admin_page_calls(string actionName, string template)
    {
        var action = typeof(AdminWorkspaceBillingController).GetMethod(actionName)!;
        Assert.Equal(template, action.GetCustomAttribute<HttpMethodAttribute>()!.Template);
    }

    /// <summary>
    /// The old role-gated, unaudited adjust route is gone: one audited door to a workspace's
    /// balance, not two.
    /// </summary>
    [Fact]
    public void The_unaudited_credit_adjust_route_is_gone()
    {
        Assert.Null(typeof(CreditsController).GetMethod("AdjustWorkspaceCredits"));
        var templates = typeof(CreditsController)
            .GetMethods()
            .SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>())
            .Select(a => a.Template ?? string.Empty);
        Assert.DoesNotContain(templates, t => t.EndsWith("/adjust", StringComparison.Ordinal));
    }
}

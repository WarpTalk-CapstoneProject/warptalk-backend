using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using WarpTalk.BillingService.API.Authorization;
using WarpTalk.BillingService.API.Controllers;

namespace WarpTalk.BillingService.Tests.API.Controllers;

public class BillingEndpointSecurityContractTests
{
    [Fact]
    public void GlobalCreditHistory_IsSystemAdminOnly()
    {
        AssertAdminOnly(typeof(CreditsController), nameof(CreditsController.GetGlobalCreditHistory));
    }

    [Theory]
    [InlineData(nameof(UsagesController.GetGlobalMetrics))]
    [InlineData(nameof(UsagesController.GetGlobalUsageChart))]
    [InlineData(nameof(UsagesController.GetGlobalUsageBreakdown))]
    [InlineData(nameof(UsagesController.GetTopWorkspaces))]
    [InlineData(nameof(UsagesController.GetUsageAlerts))]
    [InlineData(nameof(UsagesController.GetUsageRateCard))]
    public void GlobalUsageAndRateActions_AreSystemAdminOnly(string actionName)
    {
        AssertAdminOnly(typeof(UsagesController), actionName);
    }

    [Fact]
    public void WorkspaceFeatureAdoption_RequiresWorkspaceBillingRole()
    {
        var action = GetAction(
            typeof(UsagesController),
            nameof(UsagesController.GetWorkspaceFeatureAdoption));
        var roleFilter = action.GetCustomAttribute<RequireWorkspaceRoleAttribute>();

        Assert.NotNull(roleFilter);
        Assert.NotNull(roleFilter!.Arguments);
        var roles = Assert.IsType<string[]>(Assert.Single(roleFilter.Arguments!));
        Assert.Contains("Owner", roles);
        Assert.Contains("Admin", roles);
        Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public void RecordUsage_IsNotAvailableToOrdinaryWorkspaceUsers()
    {
        AssertAdminOnly(
            typeof(UsagesController),
            nameof(UsagesController.RecordUsage));
    }

    [Fact]
    public void GlobalInvoiceHistory_IsSystemAdminOnly()
    {
        AssertAdminOnly(typeof(InvoicesController), nameof(InvoicesController.GetGlobalInvoices));
    }

    [Theory]
    [InlineData(typeof(PlansController), nameof(PlansController.CreatePlan))]
    [InlineData(typeof(CreditsController), nameof(CreditsController.ManualAdjustCredits))]
    [InlineData(typeof(SubscriptionsController), nameof(SubscriptionsController.GetGlobalSubscriptions))]
    public void GlobalBillingMutationAndReportingActions_AreSystemAdminOnly(
        Type controller,
        string actionName)
    {
        AssertAdminOnly(controller, actionName);
    }

    [Fact]
    public void CreateSubscription_RequiresWorkspaceBillingRole()
    {
        AssertWorkspaceBillingRole(
            typeof(SubscriptionsController),
            nameof(SubscriptionsController.CreateSubscription));
    }

    private static void AssertAdminOnly(Type controller, string actionName)
    {
        var action = GetAction(controller, actionName);
        var authorize = action.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        var roles = authorize!.Roles!.Split(',', StringSplitOptions.TrimEntries);
        Assert.Contains(WarpTalk.Shared.WorkspaceRoleConstants.Admin, roles);
        Assert.Contains(WarpTalk.Shared.WorkspaceRoleConstants.SystemAdmin, roles);
        Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    private static void AssertAdminOrBillingAdmin(Type controller, string actionName)
    {
        var action = GetAction(controller, actionName);
        var authorize = action.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        var roles = authorize!.Roles!.Split(',', StringSplitOptions.TrimEntries);
        Assert.Contains("Admin", roles);
        Assert.Contains("billing_admin", roles);
        Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    private static void AssertWorkspaceBillingRole(Type controller, string actionName)
    {
        var action = GetAction(controller, actionName);
        var roleFilter = action.GetCustomAttribute<RequireWorkspaceRoleAttribute>();

        Assert.NotNull(roleFilter);
        Assert.NotNull(roleFilter!.Arguments);
        var roles = Assert.IsType<string[]>(Assert.Single(roleFilter.Arguments!));
        Assert.Contains("Owner", roles);
        Assert.Contains("Admin", roles);
        Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    private static MethodInfo GetAction(Type controller, string actionName) =>
        controller.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == actionName);
}

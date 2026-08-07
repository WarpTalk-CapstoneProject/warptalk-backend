using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.BillingService.API.Authorization;
using WarpTalk.BillingService.API.Controllers;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

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
    [InlineData(nameof(UsagesController.UpsertUsageRateCard))]
    [InlineData(nameof(UsagesController.GetPricingConfig))]
    [InlineData(nameof(UsagesController.UpdatePricingConfig))]
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
    public void GlobalInvoiceHistory_IsSystemAdminOnly()
    {
        AssertAdminOnly(typeof(InvoicesController), nameof(InvoicesController.GetGlobalInvoices));
    }

    /// <summary>
    /// WT-260: this action used [Authorize(Roles = ...)], which authorizes off JWT role claims.
    /// A WarpTalk workspace role is per-workspace membership data resolved through
    /// workspace-service and is never a claim in the token, so a workspace Owner could never
    /// pass and the request 403'd before reaching any filter.
    /// </summary>
    [Fact]
    public void WorkspaceInvoiceHistory_RequiresWorkspaceBillingRole_NotJwtRoleClaims()
    {
        AssertWorkspaceBillingRole(
            typeof(InvoicesController),
            nameof(InvoicesController.GetWorkspaceInvoices));

        var action = GetAction(
            typeof(InvoicesController),
            nameof(InvoicesController.GetWorkspaceInvoices));
        var authorize = action.GetCustomAttribute<AuthorizeAttribute>();

        Assert.True(
            authorize is null || string.IsNullOrEmpty(authorize.Roles),
            "Workspace-scoped billing actions must not authorize off JWT role claims.");
    }

    /// <summary>
    /// WT-260 was fixed on Credits, Invoices, SalesInquiries, Subscriptions and Usages, and
    /// missed on Payments — which is the endpoint behind plan checkout and credit top-up, so
    /// every real workspace Owner got a 403 on both. These two actions are workspace-scoped and
    /// must resolve the role through workspace-service, never off JWT role claims.
    /// </summary>
    [Theory]
    [InlineData(nameof(PaymentsController.GetPaymentHistory))]
    [InlineData(nameof(PaymentsController.CreateCheckoutSession))]
    public void WorkspaceScopedPaymentActions_RequireWorkspaceBillingRole_NotJwtRoleClaims(
        string actionName)
    {
        AssertWorkspaceBillingRole(typeof(PaymentsController), actionName);

        var action = GetAction(typeof(PaymentsController), actionName);
        var authorize = action.GetCustomAttribute<AuthorizeAttribute>();

        Assert.True(
            authorize is null || string.IsNullOrEmpty(authorize.Roles),
            $"{actionName} must not authorize off JWT role claims: a WarpTalk workspace role is "
            + "per-workspace membership data and is never a claim in the token.");
    }

    /// <summary>
    /// The filter finds the workspace id in the request body rather than the route, which only
    /// works while the request implements <see cref="IWorkspaceScopedRequest"/>. Dropping that
    /// interface would silently turn the guard into a 400 on every checkout.
    /// </summary>
    [Fact]
    public void CreateCheckoutSessionRequest_CarriesItsWorkspaceScope()
    {
        Assert.True(
            typeof(IWorkspaceScopedRequest).IsAssignableFrom(typeof(CreateCheckoutSessionRequest)),
            "CreateCheckoutSessionRequest must implement IWorkspaceScopedRequest so the "
            + "workspace-role filter can resolve the workspace from the body.");
    }

    /// <summary>
    /// WT-260 was found and fixed one controller at a time, which is how Payments stayed broken
    /// for five rounds. This sweeps every billing action instead: none may authorize off a JWT
    /// role claim that is really a workspace role. Platform roles ("Admin"/"admin") stay
    /// allowed — those genuinely are token claims.
    /// </summary>
    [Fact]
    public void NoBillingAction_AuthorizesWorkspaceRolesOffJwtClaims()
    {
        // InvoicesController.CreateInvoiceCheckout is the one remaining instance of the WT-260
        // shape. It is not reachable from the web client and cannot use RequireWorkspaceRole as
        // written: its only route argument is an invoiceId, which the filter would happily treat
        // as a workspace id. Fixing it needs an invoice -> workspace lookup in the application
        // layer, so it is named here rather than silently passing. Do not add entries.
        var knownOffenders = new[] { "InvoicesController.CreateInvoiceCheckout" };

        var controllers = typeof(PaymentsController).Assembly
            .GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract);

        var offenders = controllers
            .SelectMany(controller => controller.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .Select(action => new
            {
                Action = action,
                Roles = action.GetCustomAttribute<AuthorizeAttribute>()?.Roles,
            })
            .Where(entry => !string.IsNullOrEmpty(entry.Roles))
            .Where(entry => entry.Roles!
                .Split(',', StringSplitOptions.TrimEntries)
                .Contains(WarpTalk.Shared.WorkspaceRoleConstants.Owner))
            .Select(entry => $"{entry.Action.DeclaringType!.Name}.{entry.Action.Name}")
            .Except(knownOffenders)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void PaymentsController_DoesNotExposeLegacyUnsignedWebhook()
    {
        var legacyUnsignedWebhookActions = typeof(PaymentsController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttributes<HttpPostAttribute>()
                .Any(attribute => string.Equals(attribute.Template, "webhook", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.Empty(legacyUnsignedWebhookActions);
    }

    [Theory]
    [InlineData(typeof(PlansController), nameof(PlansController.UpdatePlan))]
    [InlineData(typeof(SubscriptionsController), nameof(SubscriptionsController.GetGlobalSubscriptions))]
    public void GlobalBillingMutationAndReportingActions_AreSystemAdminOnly(
        Type controller,
        string actionName)
    {
        AssertAdminOnly(controller, actionName);
    }

    [Fact]
    public void CreateTrialSubscription_RequiresWorkspaceBillingRole()
    {
        AssertWorkspaceBillingRole(
            typeof(SubscriptionsController),
            nameof(SubscriptionsController.CreateTrialSubscription));
    }

    [Fact]
    public void CreateWorkspaceSalesInquiry_RequiresAuthAndWorkspaceBillingRole()
    {
        var action = GetAction(
            typeof(SalesInquiriesController),
            nameof(SalesInquiriesController.SubmitWorkspaceSalesInquiry));

        Assert.NotNull(action.GetCustomAttribute<AuthorizeAttribute>());
        AssertWorkspaceBillingRole(
            typeof(SalesInquiriesController),
            nameof(SalesInquiriesController.SubmitWorkspaceSalesInquiry));
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

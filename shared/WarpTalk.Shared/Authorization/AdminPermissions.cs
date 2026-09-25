using System;
using System.Collections.Generic;
using System.Linq;

namespace WarpTalk.Shared.Authorization;

/// <summary>
/// Every permission a platform staff member can hold, derived from the admin endpoints that exist.
///
/// The list is not designed top-down. Each code is the answer to "what does this endpoint let a
/// staff member do", and every admin endpoint in every service carries exactly one of them through
/// <see cref="RequirePermissionAttribute"/> — AdminEndpointPermissionCoverage fails a service's
/// tests when an endpoint has none, two, or a code that is not listed here. So a permission here
/// with no endpoint is a promise nobody keeps, and an endpoint without one does not build green.
///
/// The codes are persisted (<c>auth.permissions.code</c>, <c>auth.role_permissions</c>) and read
/// by the web to hide what a staff member cannot use. Treat them as a contract: add, never rename.
/// </summary>
public static class AdminPermissions
{
    public const string WorkspacesRead = "workspaces.read";
    public const string WorkspacesWrite = "workspaces.write";
    public const string WorkspacesLifecycle = "workspaces.lifecycle";

    public const string AccountsRead = "accounts.read";
    public const string AccountsManage = "accounts.manage";

    public const string MeetingsRead = "meetings.read";

    public const string BillingRead = "billing.read";
    public const string BillingAdjustCredit = "billing.adjust_credit";
    public const string BillingPaymentsManage = "billing.payments_manage";
    public const string BillingSubscriptionsManage = "billing.subscriptions_manage";
    public const string BillingPlansManage = "billing.plans_manage";
    public const string BillingPricingManage = "billing.pricing_manage";
    public const string BillingLeadsManage = "billing.leads_manage";
    /// <summary>G11: credit packs, add-ons and coupons (/admin/packages), including their Stripe sync.</summary>
    public const string BillingPackagesManage = "billing.packages_manage";

    public const string PluginsRead = "plugins.read";
    public const string PluginsManage = "plugins.manage";

    public const string ContentAnnouncements = "content.announcements";
    public const string ContentEmailTemplates = "content.email_templates";

    public const string GlossaryRead = "glossary.read";
    public const string GlossaryManage = "glossary.manage";

    public const string SettingsRead = "settings.read";
    public const string SettingsManage = "settings.manage";

    public const string HealthRead = "health.read";
    public const string HealthOperate = "health.operate";
    public const string ProvidersRead = "providers.read";

    public const string AuditRead = "audit.read";
    public const string AuditExport = "audit.export";

    public const string StaffRead = "staff.read";
    public const string StaffManage = "staff.manage";

    public const string WarpBotUse = "warpbot.use";

    public const string FinanceRead = "finance.read";
    public const string FinanceManage = "finance.manage";

    public const string InboxRead = "inbox.read";
    public const string InboxManage = "inbox.manage";

    /// <summary>Areas, in the order the permission matrix shows them.</summary>
    public static class Areas
    {
        public const string Workspaces = "workspaces";
        public const string Accounts = "accounts";
        public const string Meetings = "meetings";
        public const string Billing = "billing";
        public const string Plugins = "plugins";
        public const string Content = "content";
        public const string Glossary = "glossary";
        public const string Settings = "settings";
        public const string Operations = "operations";
        public const string Audit = "audit";
        public const string Staff = "staff";
        public const string Assistant = "assistant";
        public const string Finance = "finance";
        public const string Inbox = "inbox";

        public static readonly string[] Ordered =
        [
            Workspaces, Accounts, Meetings, Billing, Finance, Plugins, Content, Glossary, Settings,
            Operations, Inbox, Audit, Staff, Assistant,
        ];
    }

    /// <summary>
    /// One permission. <paramref name="IsRead"/> marks the codes that only ever reveal data, which
    /// is what the Read-only Auditor role is built from and what the matrix labels "view".
    /// </summary>
    public sealed record Definition(string Code, string Area, string Description, bool IsRead);

    public static readonly IReadOnlyList<Definition> Definitions =
    [
        new(WorkspacesRead, Areas.Workspaces, "View workspaces, their members and the admin timeline.", true),
        new(WorkspacesWrite, Areas.Workspaces, "Add internal notes, send notices and export a workspace's data.", false),
        new(WorkspacesLifecycle, Areas.Workspaces, "Suspend, reactivate, delete a workspace or transfer its ownership.", false),

        new(AccountsRead, Areas.Accounts, "View platform accounts and the voice-consent summary.", true),
        new(AccountsManage, Areas.Accounts, "Sign accounts out, deactivate, reactivate and unlock them.", false),

        new(MeetingsRead, Areas.Meetings, "View the meetings directory, meeting insights and meeting feedback.", true),

        new(BillingRead, Areas.Billing, "View revenue insights, subscriptions, invoices, usage, rate cards, plans and sales leads.", true),
        new(BillingAdjustCredit, Areas.Billing, "Grant or deduct a workspace's credits.", false),
        new(BillingPaymentsManage, Areas.Billing, "Record a manual payment and mark an invoice paid.", false),
        new(BillingSubscriptionsManage, Areas.Billing, "Change plans, extend trials, comp periods, set entitlements and contract terms.", false),
        new(BillingPlansManage, Areas.Billing, "Create and edit sellable plans.", false),
        new(BillingPricingManage, Areas.Billing, "Edit rate cards, provider costs, the pricing configuration and the FX rate.", false),
        new(BillingLeadsManage, Areas.Billing, "Move enterprise sales leads through their statuses.", false),
        new(BillingPackagesManage, Areas.Billing, "Create, edit, archive and sync to Stripe the credit packs, add-ons and coupons.", false),

        new(PluginsRead, Areas.Plugins, "View the plugin marketplace catalog and per-workspace availability.", true),
        new(PluginsManage, Areas.Plugins, "Add, edit, retire and delete plugins and set per-workspace availability.", false),

        new(ContentAnnouncements, Areas.Content, "Write, publish and archive platform announcements.", false),
        new(ContentEmailTemplates, Areas.Content, "Edit, restore and test-send transactional email templates.", false),

        new(GlossaryRead, Areas.Glossary, "View the platform glossary and its history.", true),
        new(GlossaryManage, Areas.Glossary, "Create, edit, publish, archive and import platform glossary terms.", false),

        new(SettingsRead, Areas.Settings, "View the language catalog and the billing policy.", true),
        new(SettingsManage, Areas.Settings, "Edit the language catalog and the billing policy (VAT).", false),

        new(HealthRead, Areas.Operations, "View system health, Grafana dashboards and dead-lettered events.", true),
        new(HealthOperate, Areas.Operations, "Replay dead-lettered events.", false),
        new(ProvidersRead, Areas.Operations, "View AI provider cost, latency and uptime.", true),

        new(AuditRead, Areas.Audit, "Read the platform audit log.", true),
        new(AuditExport, Areas.Audit, "Export the platform audit log.", false),

        new(StaffRead, Areas.Staff, "View staff members, roles and who holds each permission.", true),
        new(StaffManage, Areas.Staff, "Invite staff, change their role, suspend or remove them, and edit roles.", false),

        new(WarpBotUse, Areas.Assistant, "Use the platform WarpBot assistant.", false),

        new(FinanceRead, Areas.Finance, "View operating expenses, budgets, expense reports and the profit and loss with expenses.", true),
        new(FinanceManage, Areas.Finance, "Record, edit, import and delete operating expenses, receipts, categories and budgets.", false),

        new(InboxRead, Areas.Inbox, "View the pending-work inbox (each item only from the areas the member can view).", true),
        new(InboxManage, Areas.Inbox, "Assign, snooze, annotate and close items in the pending-work inbox.", false),
    ];

    public static readonly IReadOnlyList<string> All = Definitions.Select(d => d.Code).ToArray();

    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);

    public static bool IsKnown(string? code) => code is not null && Known.Contains(code);

    public static Definition? Find(string? code) =>
        code is null ? null : Definitions.FirstOrDefault(d => string.Equals(d.Code, code, StringComparison.Ordinal));
}

/// <summary>
/// The roles every environment starts with. Seeded by
/// <c>auth/database/migrations/20260925090000_add_platform_staff_rbac.sql</c> and the permission
/// migrations after it (20260925170000_add_billing_packages_manage_permission.sql); this list is what
/// the seed must agree with, and a test holds the two together.
///
/// Built-in roles are read-only in the portal: they can be duplicated into a custom role, never
/// edited or deleted. Super Admin holds every permission by rule, not by rows — a permission added
/// next month is Super Admin's without a migration, which is what "keeps full access" has to mean.
/// </summary>
public static class BuiltInStaffRoles
{
    public const string SuperAdmin = "super_admin";
    public const string BillingFinance = "billing_finance";
    public const string Support = "support";
    public const string ContentMarketing = "content_marketing";
    public const string OperationsSre = "operations_sre";
    public const string ReadOnlyAuditor = "read_only_auditor";

    public sealed record Definition(string Slug, string Name, string Description, IReadOnlyList<string> Permissions);

    public static readonly IReadOnlyList<Definition> Definitions =
    [
        new(SuperAdmin, "Super Admin",
            "Every permission, including managing staff. Cannot be edited.",
            AdminPermissions.All),
        new(BillingFinance, "Billing / Finance",
            "Revenue, subscriptions, credits, invoices, plans and pricing.",
            [
                AdminPermissions.BillingRead, AdminPermissions.BillingAdjustCredit,
                AdminPermissions.BillingPaymentsManage, AdminPermissions.BillingSubscriptionsManage,
                AdminPermissions.BillingPlansManage, AdminPermissions.BillingPricingManage,
                AdminPermissions.BillingLeadsManage, AdminPermissions.BillingPackagesManage,
                AdminPermissions.WorkspacesRead,
                AdminPermissions.AccountsRead, AdminPermissions.ProvidersRead,
                AdminPermissions.SettingsRead, AdminPermissions.AuditRead, AdminPermissions.WarpBotUse,
                AdminPermissions.FinanceRead, AdminPermissions.FinanceManage,
                AdminPermissions.InboxRead, AdminPermissions.InboxManage,
            ]),
        new(Support, "Support",
            "Helps customers: workspaces, accounts and meetings, with read access to billing.",
            [
                AdminPermissions.WorkspacesRead, AdminPermissions.WorkspacesWrite,
                AdminPermissions.AccountsRead, AdminPermissions.AccountsManage,
                AdminPermissions.MeetingsRead, AdminPermissions.BillingRead,
                AdminPermissions.PluginsRead, AdminPermissions.GlossaryRead,
                AdminPermissions.SettingsRead, AdminPermissions.AuditRead, AdminPermissions.WarpBotUse,
                AdminPermissions.InboxRead, AdminPermissions.InboxManage,
            ]),
        new(ContentMarketing, "Content / Marketing",
            "Announcements, email templates and the platform glossary.",
            [
                AdminPermissions.ContentAnnouncements, AdminPermissions.ContentEmailTemplates,
                AdminPermissions.GlossaryRead, AdminPermissions.GlossaryManage,
                AdminPermissions.WorkspacesRead, AdminPermissions.MeetingsRead,
                AdminPermissions.SettingsRead, AdminPermissions.WarpBotUse,
                AdminPermissions.InboxRead, AdminPermissions.InboxManage,
            ]),
        new(OperationsSre, "Operations / SRE",
            "System health, providers, the plugin catalog and platform settings.",
            [
                AdminPermissions.HealthRead, AdminPermissions.HealthOperate,
                AdminPermissions.ProvidersRead, AdminPermissions.PluginsRead,
                AdminPermissions.PluginsManage, AdminPermissions.SettingsRead,
                AdminPermissions.SettingsManage, AdminPermissions.WorkspacesRead,
                AdminPermissions.AccountsRead, AdminPermissions.MeetingsRead,
                AdminPermissions.AuditRead, AdminPermissions.WarpBotUse,
                AdminPermissions.InboxRead, AdminPermissions.InboxManage,
            ]),
        new(ReadOnlyAuditor, "Read-only Auditor",
            "Sees everything, changes nothing. Can export the audit log.",
            AdminPermissions.Definitions.Where(d => d.IsRead).Select(d => d.Code)
                .Append(AdminPermissions.AuditExport).ToArray()),
    ];

    public static Definition? Find(string? slug) =>
        slug is null ? null : Definitions.FirstOrDefault(d => string.Equals(d.Slug, slug, StringComparison.Ordinal));
}

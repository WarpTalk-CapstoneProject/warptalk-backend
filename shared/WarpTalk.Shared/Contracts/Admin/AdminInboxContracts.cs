using System;
using System.Collections.Generic;

namespace WarpTalk.Shared.Contracts.Admin;

/// <summary>
/// The pending-work inbox (G12, /admin/inbox). Each service that owns something waiting on platform
/// staff answers <c>GET …/inbox-items</c> with the items it holds RIGHT NOW, in this shape, gated by the
/// permission that already lets a staff member see that thing. The workspace service fans out to every
/// source with the caller's own token, so a staff member only ever sees items from areas their role can
/// read, and a source that is down or slow degrades to "unavailable" instead of failing the inbox.
///
/// Items are never stored by their source: an item exists while its condition holds and disappears the
/// moment it no longer does (a lead leaves "new", an invoice is paid, a dead letter is replayed). Only
/// triage — assignment, snooze, notes, and "done" for items with no natural completion — is persisted,
/// by the aggregator, keyed on <see cref="AdminInboxItem.Key"/>.
/// </summary>
public static class AdminInbox
{
    public static class Sources
    {
        public const string Billing = "billing";
        public const string Providers = "providers";
        public const string Expenses = "expenses";
        public const string Staff = "staff";
        public const string Content = "content";
        public const string Operations = "operations";

        public static readonly IReadOnlyList<string> All = [Billing, Providers, Expenses, Staff, Content, Operations];
    }

    public static class Types
    {
        public const string SalesLead = "sales_lead";
        public const string InvoicePastDue = "invoice_past_due";
        public const string InvoiceAwaitingPayment = "invoice_awaiting_payment";
        public const string PaymentDisputed = "payment_disputed";
        public const string TrialEnding = "trial_ending";
        public const string SubscriptionEnding = "subscription_ending";
        public const string SubscriptionSuspended = "subscription_suspended";
        public const string ProviderIncident = "provider_incident";
        public const string ProviderQuota = "provider_quota";
        public const string ExpenseDue = "expense_due";
        public const string StaffInvitation = "staff_invitation";
        public const string AnnouncementScheduled = "announcement_scheduled";
        public const string AnnouncementDraft = "announcement_draft";
        public const string BroadcastFailed = "broadcast_failed";
        public const string DeadLetter = "dead_letter";

        public static readonly IReadOnlyList<string> All =
        [
            SalesLead, InvoicePastDue, InvoiceAwaitingPayment, PaymentDisputed, TrialEnding, SubscriptionEnding,
            SubscriptionSuspended, ProviderIncident, ProviderQuota, ExpenseDue, StaffInvitation,
            AnnouncementScheduled, AnnouncementDraft, BroadcastFailed, DeadLetter,
        ];
    }

    public static class Priorities
    {
        public const string Low = "low";
        public const string Normal = "normal";
        public const string High = "high";
        public const string Urgent = "urgent";

        public static readonly IReadOnlyList<string> All = [Low, Normal, High, Urgent];

        public static int Rank(string? priority) => priority switch
        {
            Urgent => 3,
            High => 2,
            Normal => 1,
            _ => 0,
        };
    }

    /// <summary>The largest number of items one source returns; the oldest beyond it are left for later.</summary>
    public const int MaxItemsPerSource = 200;
}

/// <summary>
/// One piece of pending work, as its owning service describes it.
/// </summary>
/// <param name="Key">Stable across calls while the condition holds: <c>{type}:{id}</c>, plus a period when the
/// same thing can recur (e.g. <c>provider_quota:cartesia:2026-09-25</c>). Triage state is keyed on it.</param>
/// <param name="Type">An <see cref="AdminInbox.Types"/> value.</param>
/// <param name="Title">One line, in English; the web localizes by <paramref name="Type"/> and shows this as the detail.</param>
/// <param name="Detail">A second line: the amount, the error, the addressee.</param>
/// <param name="WorkspaceId">The customer the item concerns, when there is one.</param>
/// <param name="Customer">The workspace name or company, when known.</param>
/// <param name="OccurredAt">When the thing started waiting: the age column counts from here.</param>
/// <param name="DueAt">The SLA: when it must be handled by. Past it, the item is overdue.</param>
/// <param name="Priority">An <see cref="AdminInbox.Priorities"/> value.</param>
/// <param name="Href">The admin page that resolves it (a path under /admin).</param>
/// <param name="NaturalCompletion">True when the item leaves the inbox by itself once the work is done in its
/// source; false when only "mark done" can close it.</param>
/// <param name="Amount">A money figure worth showing, with <paramref name="Currency"/>.</param>
public sealed record AdminInboxItem(
    string Key,
    string Type,
    string Title,
    string? Detail,
    Guid? WorkspaceId,
    string? Customer,
    DateTime OccurredAt,
    DateTime? DueAt,
    string Priority,
    string Href,
    bool NaturalCompletion,
    decimal? Amount = null,
    string? Currency = null);

/// <summary>What one source answers. <paramref name="Truncated"/> says there were more than it returned.</summary>
public sealed record AdminInboxSourceResponse(
    string Source,
    DateTime GeneratedAt,
    IReadOnlyList<AdminInboxItem> Items,
    bool Truncated = false);

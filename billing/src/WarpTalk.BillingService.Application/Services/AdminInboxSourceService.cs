using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>
/// The pending-work inbox, billing's three sources (G12). Each answers what is waiting RIGHT NOW; nothing
/// here is stored, so an item leaves the inbox the moment its condition stops holding.
///
///   billing   — new sales leads, past-due invoices, internal invoices awaiting payment confirmation
///               (the bank-transfer path: provider internal_invoice), disputed payments, trials ending
///               within 7 days, subscriptions ending within 14 days without auto-renew, suspended service.
///   providers — status-page incidents still open, and 402 quota refusals in the last 24 hours.
///   expenses  — planned operating expenses due within 7 days or overdue.
/// </summary>
public interface IAdminInboxSourceService
{
    Task<AdminInboxSourceResponse> GetBillingItemsAsync(CancellationToken ct = default);
    Task<AdminInboxSourceResponse> GetProviderItemsAsync(CancellationToken ct = default);
    Task<AdminInboxSourceResponse> GetExpenseItemsAsync(CancellationToken ct = default);
}

public sealed class AdminInboxSourceService : IAdminInboxSourceService
{
    public const int TrialWindowDays = 7;
    public const int RenewalWindowDays = 14;
    public const int DisputeLookbackDays = 60;
    public const int IncidentLookbackDays = 14;
    public const int ExpenseLeadDays = 7;
    public const int FrozenPurchaseLookbackDays = 30;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceClient _workspaceClient;
    private readonly TimeProvider _time;
    private readonly ILogger<AdminInboxSourceService> _logger;

    public AdminInboxSourceService(
        IUnitOfWork unitOfWork,
        IWorkspaceClient workspaceClient,
        ILogger<AdminInboxSourceService> logger,
        TimeProvider? time = null)
    {
        _unitOfWork = unitOfWork;
        _workspaceClient = workspaceClient;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<AdminInboxSourceResponse> GetBillingItemsAsync(CancellationToken ct = default)
    {
        var now = Now;
        var take = AdminInbox.MaxItemsPerSource;
        var items = new List<Draft>();

        var leads = await _unitOfWork.SalesInquiryRepository.GetPagedAsync(
            lead => lead.Status == SalesInquiryConstants.Statuses.New, 0, take, q => q.OrderBy(lead => lead.CreatedAt), ct);
        foreach (var lead in leads)
        {
            var person = $"{lead.FirstName} {lead.LastName}".Trim();
            var title = string.IsNullOrWhiteSpace(lead.Company) ? person : $"{lead.Company} — {person}";
            items.Add(new Draft(
                $"{AdminInbox.Types.SalesLead}:{lead.Id}",
                AdminInbox.Types.SalesLead,
                title,
                $"{lead.RequestType} · {lead.WorkEmail}",
                lead.WorkspaceId,
                string.IsNullOrWhiteSpace(lead.Company) ? null : lead.Company,
                Utc(lead.CreatedAt),
                Utc(lead.CreatedAt).AddHours(24),
                now - Utc(lead.CreatedAt) > TimeSpan.FromHours(24) ? AdminInbox.Priorities.High : AdminInbox.Priorities.Normal,
                "/admin/sales-leads?status=new&q=" + Uri.EscapeDataString(lead.WorkEmail),
                NaturalCompletion: true));
        }

        foreach (var invoice in await _unitOfWork.InvoiceRepository.GetOutstandingForInboxAsync(take, ct))
        {
            var due = invoice.DueAt is { } d ? Utc(d) : (DateTime?)null;
            var amount = Money(invoice.Total, invoice.Currency);
            if (due is { } pastDue && pastDue < now)
            {
                var daysOver = (int)Math.Floor((now - pastDue).TotalDays);
                items.Add(new Draft(
                    $"{AdminInbox.Types.InvoicePastDue}:{invoice.InvoiceId}",
                    AdminInbox.Types.InvoicePastDue,
                    $"Invoice {invoice.InvoiceNumber} is past due",
                    string.Create(Invariant, $"{amount}, {daysOver} day{(daysOver == 1 ? "" : "s")} overdue"),
                    invoice.WorkspaceId,
                    null,
                    pastDue,
                    pastDue.AddDays(3),
                    daysOver >= 14 ? AdminInbox.Priorities.Urgent : AdminInbox.Priorities.High,
                    WorkspaceHref(invoice.WorkspaceId),
                    NaturalCompletion: true,
                    invoice.Total,
                    invoice.Currency));
            }
            else if (string.Equals(invoice.Provider, PaymentConstants.Providers.InternalInvoice, StringComparison.OrdinalIgnoreCase))
            {
                // An enterprise invoice paid by bank transfer: only staff can confirm the money arrived.
                items.Add(new Draft(
                    $"{AdminInbox.Types.InvoiceAwaitingPayment}:{invoice.InvoiceId}",
                    AdminInbox.Types.InvoiceAwaitingPayment,
                    $"Confirm payment of invoice {invoice.InvoiceNumber}",
                    due is { } dueAt ? string.Create(Invariant, $"{amount}, due {dueAt:yyyy-MM-dd}") : amount,
                    invoice.WorkspaceId,
                    null,
                    Utc(invoice.IssuedAt),
                    due,
                    AdminInbox.Priorities.Normal,
                    WorkspaceHref(invoice.WorkspaceId),
                    NaturalCompletion: true,
                    invoice.Total,
                    invoice.Currency));
            }
        }

        foreach (var payment in await _unitOfWork.PaymentRepository.GetDisputedSinceAsync(now.AddDays(-DisputeLookbackDays), take, ct))
        {
            items.Add(new Draft(
                $"{AdminInbox.Types.PaymentDisputed}:{payment.PaymentId}",
                AdminInbox.Types.PaymentDisputed,
                "Payment disputed by the card holder",
                $"{Money(payment.TotalAmount, payment.Currency)} via {payment.Provider}",
                payment.WorkspaceId,
                null,
                Utc(payment.UpdatedAt),
                Utc(payment.UpdatedAt).AddDays(7),
                AdminInbox.Priorities.Urgent,
                WorkspaceHref(payment.WorkspaceId),
                // A dispute stays "disputed" on our side whatever Stripe decides, so only a person can close it.
                NaturalCompletion: false,
                payment.TotalAmount,
                payment.Currency));
        }

        foreach (var subscription in await _unitOfWork.SubscriptionRepository.GetNeedingAttentionAsync(now, now.AddDays(RenewalWindowDays), take, ct))
        {
            if (subscription.ServiceState == SubscriptionConstants.ServiceStates.Suspended)
            {
                items.Add(new Draft(
                    $"{AdminInbox.Types.SubscriptionSuspended}:{subscription.SubscriptionId}:{subscription.SuspendedReason ?? "unknown"}",
                    AdminInbox.Types.SubscriptionSuspended,
                    $"Service suspended on {subscription.PlanName}",
                    subscription.SuspendedReason is null ? null : $"Reason: {subscription.SuspendedReason.Replace('_', ' ')}",
                    subscription.WorkspaceId,
                    null,
                    Utc(subscription.UpdatedAt),
                    Utc(subscription.UpdatedAt).AddDays(1),
                    AdminInbox.Priorities.High,
                    WorkspaceHref(subscription.WorkspaceId),
                    NaturalCompletion: true));
            }
            else if (subscription.TrialEndsAt is { } trialEnds && Utc(trialEnds) > now && Utc(trialEnds) <= now.AddDays(TrialWindowDays))
            {
                var ends = Utc(trialEnds);
                items.Add(new Draft(
                    $"{AdminInbox.Types.TrialEnding}:{subscription.SubscriptionId}:{ends:yyyyMMdd}",
                    AdminInbox.Types.TrialEnding,
                    $"Trial of {subscription.PlanName} ends {ends:yyyy-MM-dd}",
                    null,
                    subscription.WorkspaceId,
                    null,
                    ends.AddDays(-TrialWindowDays),
                    ends,
                    ends - now <= TimeSpan.FromDays(2) ? AdminInbox.Priorities.High : AdminInbox.Priorities.Normal,
                    WorkspaceHref(subscription.WorkspaceId),
                    // Follow-up work: the trial ending does not tell anyone the customer was contacted.
                    NaturalCompletion: false));
            }
            else if (!subscription.AutoRenew)
            {
                var ends = Utc(subscription.CurrentPeriodEnd);
                items.Add(new Draft(
                    $"{AdminInbox.Types.SubscriptionEnding}:{subscription.SubscriptionId}:{ends:yyyyMMdd}",
                    AdminInbox.Types.SubscriptionEnding,
                    $"{subscription.PlanName} ends {ends:yyyy-MM-dd} and will not renew",
                    null,
                    subscription.WorkspaceId,
                    null,
                    ends.AddDays(-RenewalWindowDays),
                    ends,
                    ends - now <= TimeSpan.FromDays(3) ? AdminInbox.Priorities.High : AdminInbox.Priorities.Normal,
                    WorkspaceHref(subscription.WorkspaceId),
                    NaturalCompletion: false));
            }
        }

        // backend#467: paid credits that arrived with no live subscription and were booked frozen.
        // Stays until the credits are released (a renewal) or adjusted away by support.
        var frozenSince = now.AddDays(-FrozenPurchaseLookbackDays);
        var frozenPurchases = await _unitOfWork.CreditTransactionRepository.FindAsync(
            t => t.ReferenceType == TransactionConstants.ReferenceTypes.FrozenPurchase && t.CreatedAt >= frozenSince,
            ct) ?? Array.Empty<Domain.Entities.CreditTransaction>();
        foreach (var entry in frozenPurchases.OrderBy(t => t.CreatedAt).Take(take))
        {
            var holder = await _unitOfWork.SubscriptionRepository.GetByIdAsync(entry.SubscriptionId, ct);
            if (holder is null || holder.FrozenCredits <= 0)
            {
                continue;
            }

            items.Add(new Draft(
                $"{AdminInbox.Types.PaidCreditsFrozen}:{entry.Id}",
                AdminInbox.Types.PaidCreditsFrozen,
                string.Create(Invariant, $"{entry.Amount:N0} paid credits kept frozen: no live subscription"),
                entry.Description,
                entry.WorkspaceId,
                null,
                Utc(entry.CreatedAt),
                Utc(entry.CreatedAt).AddDays(1),
                AdminInbox.Priorities.Urgent,
                WorkspaceHref(entry.WorkspaceId),
                NaturalCompletion: true));
        }

        return await RespondAsync(AdminInbox.Sources.Billing, items, now, ct);
    }

    public async Task<AdminInboxSourceResponse> GetProviderItemsAsync(CancellationToken ct = default)
    {
        var now = Now;
        var items = new List<Draft>();

        foreach (var incident in await _unitOfWork.ProviderStatusIncidents.GetUnresolvedSinceAsync(now.AddDays(-IncidentLookbackDays), ct))
        {
            var started = Utc(incident.StartedAt);
            items.Add(new Draft(
                $"{AdminInbox.Types.ProviderIncident}:{incident.Provider}:{incident.ExternalId}",
                AdminInbox.Types.ProviderIncident,
                $"{ProviderName(incident.Provider)}: {incident.Name}",
                $"{incident.Impact} impact · {incident.Status}",
                null,
                ProviderName(incident.Provider),
                started,
                started.AddHours(4),
                incident.Impact switch
                {
                    "critical" => AdminInbox.Priorities.Urgent,
                    "major" => AdminInbox.Priorities.High,
                    "maintenance" or "none" => AdminInbox.Priorities.Low,
                    _ => AdminInbox.Priorities.Normal,
                },
                "/admin/providers",
                NaturalCompletion: true));
        }

        var since = now.AddHours(-24);
        var stats = await _unitOfWork.ProviderCallStats.GetRangeAsync(null, since, now, ct);
        foreach (var provider in stats.Where(s => s.Quota > 0).GroupBy(s => s.Provider))
        {
            var refused = provider.Sum(s => s.Quota);
            var first = Utc(provider.Min(s => s.HourStart));
            var last = Utc(provider.Max(s => s.HourStart));
            items.Add(new Draft(
                // One item per provider per UTC day of the latest refusal: a new day's refusals are new work.
                $"{AdminInbox.Types.ProviderQuota}:{provider.Key}:{last:yyyy-MM-dd}",
                AdminInbox.Types.ProviderQuota,
                $"{ProviderName(provider.Key)} refused calls for quota (402)",
                string.Create(Invariant, $"{refused:N0} call{(refused == 1 ? "" : "s")} in the last 24 h; top up or raise the plan"),
                null,
                ProviderName(provider.Key),
                first,
                first.AddHours(2),
                AdminInbox.Priorities.Urgent,
                "/admin/providers",
                // Refusals stop being counted 24 h after the last one; a top-up is confirmed by a person.
                NaturalCompletion: false));
        }

        return await RespondAsync(AdminInbox.Sources.Providers, items, now, ct);
    }

    public async Task<AdminInboxSourceResponse> GetExpenseItemsAsync(CancellationToken ct = default)
    {
        var now = Now;
        var today = DateOnly.FromDateTime(now);
        var items = new List<Draft>();
        foreach (var expense in await _unitOfWork.OperatingExpenses.ListPlannedDueAsync(today.AddDays(ExpenseLeadDays), ct))
        {
            var due = DateTime.SpecifyKind(expense.ExpenseDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            var overdue = expense.ExpenseDate < today;
            items.Add(new Draft(
                $"{AdminInbox.Types.ExpenseDue}:{expense.Id}",
                AdminInbox.Types.ExpenseDue,
                $"{expense.Vendor} — {Money(expense.Amount, expense.Currency)}",
                expense.RecurringSourceId is null
                    ? expense.Category?.Name
                    : $"{expense.Category?.Name} · recurring",
                null,
                expense.Vendor,
                due.AddDays(-ExpenseLeadDays),
                due,
                overdue ? AdminInbox.Priorities.High : AdminInbox.Priorities.Normal,
                "/admin/finance/expenses?q=" + Uri.EscapeDataString(expense.Vendor),
                NaturalCompletion: true,
                expense.Amount,
                expense.Currency));
        }

        return await RespondAsync(AdminInbox.Sources.Expenses, items, now, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private sealed record Draft(
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

    private async Task<AdminInboxSourceResponse> RespondAsync(string source, List<Draft> drafts, DateTime now, CancellationToken ct)
    {
        var truncated = drafts.Count > AdminInbox.MaxItemsPerSource;
        var kept = drafts
            .OrderByDescending(d => AdminInbox.Priorities.Rank(d.Priority))
            .ThenBy(d => d.DueAt ?? DateTime.MaxValue)
            .Take(AdminInbox.MaxItemsPerSource)
            .ToList();
        var names = await ResolveNamesAsync(kept.Where(d => d.WorkspaceId.HasValue).Select(d => d.WorkspaceId!.Value), ct);

        var items = kept
            .Select(d => new AdminInboxItem(
                d.Key, d.Type, d.Title, d.Detail, d.WorkspaceId,
                d.Customer ?? (d.WorkspaceId is { } id && names.TryGetValue(id, out var name) ? name : null),
                d.OccurredAt, d.DueAt, d.Priority, d.Href, d.NaturalCompletion, d.Amount, d.Currency))
            .ToList();
        return new AdminInboxSourceResponse(source, now, items, truncated);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (distinct.Length == 0) return new Dictionary<Guid, string>();
        try
        {
            var result = await _workspaceClient.GetWorkspaceNamesAsync(distinct, ct);
            if (result.IsSuccess && result.Value is not null) return result.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Names are decoration: an item without one is still work to do.
            _logger.LogWarning(ex, "Inbox could not resolve workspace names.");
        }

        return new Dictionary<Guid, string>();
    }

    private static string WorkspaceHref(Guid workspaceId) => $"/admin/workspaces/{workspaceId}";

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string Money(decimal amount, string currency)
        => string.Equals(currency, "VND", StringComparison.OrdinalIgnoreCase)
            ? string.Create(Invariant, $"{amount:N0} VND")
            : string.Create(Invariant, $"{amount:N2} {currency.ToUpperInvariant()}");

    private static string ProviderName(string provider) => provider switch
    {
        ProviderCatalog.OpenAi => "OpenAI",
        ProviderCatalog.Cartesia => "Cartesia",
        ProviderCatalog.LiveKit => "LiveKit",
        ProviderCatalog.Stripe => "Stripe",
        _ => provider,
    };
}

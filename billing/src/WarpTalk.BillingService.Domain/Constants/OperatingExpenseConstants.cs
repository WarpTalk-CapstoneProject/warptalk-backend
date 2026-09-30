using System;
using System.Collections.Generic;

namespace WarpTalk.BillingService.Domain.Constants;

/// <summary>Vocabulary of subscription.operating_expenses, expense_categories and expense_budgets (G12).</summary>
public static class OperatingExpenseConstants
{
    public static class Statuses
    {
        /// <summary>Committed but not paid yet: a bill that is coming, or an occurrence of a series.</summary>
        public const string Planned = "planned";
        public const string Paid = "paid";

        public static readonly IReadOnlyList<string> All = [Planned, Paid];
    }

    public static class Recurrences
    {
        public const string None = "none";
        public const string Monthly = "monthly";
        public const string Yearly = "yearly";

        public static readonly IReadOnlyList<string> All = [None, Monthly, Yearly];
    }

    public static class Currencies
    {
        public const string Vnd = FxRateConstants.Vnd;
        public const string Usd = FxRateConstants.Usd;

        public static readonly IReadOnlyList<string> All = [Vnd, Usd];
    }

    /// <summary>Free text is allowed; these are what the form offers.</summary>
    public static class PaymentMethods
    {
        public const string BankTransfer = "bank_transfer";
        public const string CompanyCard = "company_card";
        public const string PersonalCard = "personal_card";
        public const string Cash = "cash";
        public const string Paypal = "paypal";
        public const string Other = "other";

        public static readonly IReadOnlyList<string> All = [BankTransfer, CompanyCard, PersonalCard, Cash, Paypal, Other];
    }

    /// <summary>How many days before its due date the next occurrence of a series is written as a planned row.</summary>
    public const int RecurrenceLeadDays = 7;

    /// <summary>How far ahead the "recurring commitments" report looks.</summary>
    public const int CommitmentHorizonDays = 90;

    /// <summary>The longest range the report and the P&amp;L accept, in months.</summary>
    public const int MaxReportMonths = 24;

    /// <summary>The P&amp;L reuses the Insights P&amp;L, which accepts at most 366 days.</summary>
    public const int MaxPnlMonths = 12;

    public const int MaxTags = 10;
    public const int MaxTagLength = 40;
    public const int MaxImportRows = 2000;

    public const long MaxReceiptBytes = 10 * 1024 * 1024;

    public static readonly IReadOnlyDictionary<string, string> ReceiptContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = "application/pdf",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".webp"] = "image/webp",
            [".heic"] = "image/heic",
        };
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Application.Services.Expenses;

/// <summary>One data row of an expense CSV, as text, keyed by canonical column name.</summary>
public sealed record ExpenseCsvRow(int Line, IReadOnlyDictionary<string, string> Values)
{
    public string? Get(string column) =>
        Values.TryGetValue(column, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}

public sealed record ExpenseCsvDocument(IReadOnlyList<string> Columns, IReadOnlyList<string> UnknownColumns, IReadOnlyList<ExpenseCsvRow> Rows);

/// <summary>
/// Reads the expense import CSV (G12): RFC 4180 (quoted fields, doubled quotes, CRLF or LF, a UTF-8 BOM),
/// comma- or semicolon-separated (Excel in a Vietnamese locale writes semicolons), with a header row.
/// Header names are matched case-insensitively against the canonical names and a few aliases, so an
/// export of the list page imports back unchanged.
/// </summary>
public static class ExpenseCsv
{
    public const string Date = "date";
    public const string Vendor = "vendor";
    public const string Category = "category";
    public const string Amount = "amount";
    public const string Currency = "currency";
    public const string Description = "description";
    public const string PaymentMethod = "payment_method";
    public const string Status = "status";
    public const string PaidBy = "paid_by";
    public const string Tags = "tags";
    public const string Recurrence = "recurrence";

    public static readonly IReadOnlyList<string> Canonical =
        [Date, Vendor, Category, Amount, Currency, Description, PaymentMethod, Status, PaidBy, Tags, Recurrence];

    public static readonly IReadOnlyList<string> Required = [Date, Vendor, Category, Amount];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["expense_date"] = Date,
        ["expensedate"] = Date,
        ["supplier"] = Vendor,
        ["payee"] = Vendor,
        ["category_slug"] = Category,
        ["categoryname"] = Category,
        ["category_name"] = Category,
        ["notes"] = Description,
        ["note"] = Description,
        ["memo"] = Description,
        ["paymentmethod"] = PaymentMethod,
        ["method"] = PaymentMethod,
        ["paidby"] = PaidBy,
        ["tag"] = Tags,
        ["repeat"] = Recurrence,
    };

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy"];

    public static ExpenseCsvDocument Parse(string csv)
    {
        var text = (csv ?? string.Empty).TrimStart('﻿');
        var firstLine = text.Split('\n', 2)[0];
        var separator = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';

        var records = ReadRecords(text, separator);
        if (records.Count == 0) return new ExpenseCsvDocument([], [], []);

        var header = records[0].Fields;
        var columns = new List<string?>();
        var known = new List<string>();
        var unknown = new List<string>();
        foreach (var raw in header)
        {
            var name = Normalize(raw);
            var canonical = Canonical.Contains(name) ? name
                : Aliases.TryGetValue(name, out var alias) ? alias
                : Aliases.TryGetValue(name.Replace("_", string.Empty), out var squashed) ? squashed
                : string.Empty;
            if (string.IsNullOrEmpty(canonical) || known.Contains(canonical))
            {
                if (!string.IsNullOrWhiteSpace(raw)) unknown.Add(raw.Trim());
                columns.Add(null);
            }
            else
            {
                known.Add(canonical);
                columns.Add(canonical);
            }
        }

        var rows = new List<ExpenseCsvRow>();
        foreach (var record in records.Skip(1))
        {
            if (record.Fields.All(string.IsNullOrWhiteSpace)) continue;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < columns.Count && i < record.Fields.Count; i++)
            {
                if (columns[i] is { } column) values[column] = record.Fields[i];
            }

            rows.Add(new ExpenseCsvRow(record.Line, values));
        }

        return new ExpenseCsvDocument(known, unknown, rows);
    }

    private static string Normalize(string raw)
        => raw.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    private sealed record Record(int Line, List<string> Fields);

    private static List<Record> ReadRecords(string text, char separator)
    {
        var records = new List<Record>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var recordLine = 1;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else
                {
                    if (c == '\n') line++;
                    field.Append(c);
                }

                continue;
            }

            if (c == '"' && field.Length == 0) { inQuotes = true; continue; }
            if (c == separator) { fields.Add(field.ToString()); field.Clear(); continue; }
            if (c == '\r') continue;
            if (c == '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                records.Add(new Record(recordLine, fields));
                fields = [];
                line++;
                recordLine = line;
                continue;
            }

            field.Append(c);
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(new Record(recordLine, fields));
        }

        return records;
    }

    // ── Field parsers ─────────────────────────────────────────────────────────────────────────

    public static bool TryParseDate(string? value, out DateOnly date)
        => DateOnly.TryParseExact(value?.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>
    /// Invariant-culture numbers: "1500000", "1,500,000", "12.50". A dotted thousands separator
    /// ("1.500.000") is refused rather than guessed, because "1.500" is also a valid decimal.
    /// </summary>
    public static bool TryParseAmount(string? value, out decimal amount)
    {
        amount = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var cleaned = value.Trim().Replace(" ", string.Empty).Replace(" ", string.Empty);
        foreach (var symbol in new[] { "₫", "đ", "VND", "vnd", "$", "USD", "usd" }) cleaned = cleaned.Replace(symbol, string.Empty);
        if (cleaned.Count(c => c == '.') > 1) return false;
        return decimal.TryParse(cleaned, NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }

    public static IReadOnlyList<string> ParseTags(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([';', '|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(tag => tag.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToList();

    public static string? NormalizeCurrency(string? value)
    {
        var upper = value?.Trim().ToUpperInvariant();
        return upper switch
        {
            null or "" => OperatingExpenseConstants.Currencies.Vnd,
            "VND" or "₫" or "Đ" or "VNĐ" => OperatingExpenseConstants.Currencies.Vnd,
            "USD" or "$" or "US$" => OperatingExpenseConstants.Currencies.Usd,
            _ => null,
        };
    }
}

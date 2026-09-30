using FluentAssertions;
using WarpTalk.BillingService.Application.Services.Expenses;

namespace WarpTalk.BillingService.Tests.Application.Services.Expenses;

/// <summary>G12: recurring expense dates and the expense CSV format.</summary>
public sealed class ExpenseRecurrenceTests
{
    [Fact]
    public void Monthly_series_anchored_on_the_31st_does_not_drift_after_a_short_month()
    {
        var anchor = new DateOnly(2026, 1, 31);

        var feb = ExpenseRecurrence.NextAfter(anchor, "monthly", anchor)!.Value;
        var mar = ExpenseRecurrence.NextAfter(anchor, "monthly", feb)!.Value;
        var apr = ExpenseRecurrence.NextAfter(anchor, "monthly", mar)!.Value;

        feb.Should().Be(new DateOnly(2026, 2, 28));
        mar.Should().Be(new DateOnly(2026, 3, 31));
        apr.Should().Be(new DateOnly(2026, 4, 30));
    }

    [Fact]
    public void A_series_recorded_with_a_past_first_date_starts_from_today_instead_of_back_filling()
    {
        var anchor = new DateOnly(2026, 1, 15);
        var today = new DateOnly(2026, 9, 25);

        ExpenseRecurrence.NextAfter(anchor, "monthly", anchor, today).Should().Be(new DateOnly(2026, 10, 15));
        ExpenseRecurrence.NextAfter(new DateOnly(2026, 9, 25), "monthly", new DateOnly(2026, 9, 25), today)
            .Should().Be(new DateOnly(2026, 10, 25));
        ExpenseRecurrence.NextAfter(anchor, "yearly", anchor, today).Should().Be(new DateOnly(2027, 1, 15));
        ExpenseRecurrence.NextAfter(anchor, "none", anchor, today).Should().BeNull();
    }

    [Fact]
    public void Upcoming_stops_at_the_horizon_and_at_the_end_date()
    {
        var anchor = new DateOnly(2026, 9, 1);
        var dates = ExpenseRecurrence.Upcoming(anchor, "monthly", new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31), new DateOnly(2026, 11, 15)).ToList();

        dates.Should().Equal(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1));
    }

    [Theory]
    [InlineData(null, null, true, "2025-10", "2026-09")]
    [InlineData("2026-01", "2026-03", true, "2026-01", "2026-03")]
    [InlineData("2026-04", "2026-03", false, null, null)]
    [InlineData("2024-01", "2026-03", false, null, null)]
    [InlineData("2026-13", null, false, null, null)]
    public void Month_ranges_resolve_or_explain(string? from, string? to, bool ok, string? expectedFrom, string? expectedTo)
    {
        var resolved = ExpenseRecurrence.TryResolveMonths(from, to, new DateOnly(2026, 9, 25), 12, 24, out var f, out var t, out var error);

        resolved.Should().Be(ok);
        if (ok)
        {
            ExpenseRecurrence.MonthKey(f).Should().Be(expectedFrom);
            ExpenseRecurrence.MonthKey(t).Should().Be(expectedTo);
        }
        else
        {
            error.Should().NotBeNullOrWhiteSpace();
        }
    }
}

public sealed class ExpenseCsvTests
{
    [Fact]
    public void Parses_quoted_fields_a_bom_crlf_and_header_aliases()
    {
        var csv = "﻿Date,Supplier,Category,Amount,Currency,Notes,Extra\r\n"
                  + "2026-09-01,\"GitHub, Inc.\",saas,\"1,250.50\",USD,\"Team plan, \"\"annual\"\"\",x\r\n"
                  + "\r\n"
                  + "01/09/2026,Vietnix,servers,1500000,,VPS,\r\n";

        var document = ExpenseCsv.Parse(csv);

        document.Columns.Should().Contain(["date", "vendor", "category", "amount", "currency", "description"]);
        document.UnknownColumns.Should().Equal("Extra");
        document.Rows.Should().HaveCount(2);
        document.Rows[0].Get("vendor").Should().Be("GitHub, Inc.");
        document.Rows[0].Get("description").Should().Be("Team plan, \"annual\"");
        document.Rows[1].Line.Should().Be(4);
    }

    [Fact]
    public void Semicolon_separated_files_from_a_vietnamese_excel_are_read()
    {
        var document = ExpenseCsv.Parse("date;vendor;category;amount\n2026-09-01;Canva;saas;300000\n");

        document.Rows.Should().ContainSingle();
        document.Rows[0].Get("amount").Should().Be("300000");
    }

    [Theory]
    [InlineData("1500000", 1_500_000)]
    [InlineData("1,500,000", 1_500_000)]
    [InlineData("12.50", 12.5)]
    [InlineData("1 500 000 ₫", 1_500_000)]
    [InlineData("$49", 49)]
    public void Amounts_parse_in_the_invariant_culture(string text, decimal expected)
    {
        ExpenseCsv.TryParseAmount(text, out var amount).Should().BeTrue();
        amount.Should().Be(expected);
    }

    [Theory]
    [InlineData("1.500.000")]
    [InlineData("abc")]
    [InlineData("")]
    public void Ambiguous_or_empty_amounts_are_refused_rather_than_guessed(string text)
        => ExpenseCsv.TryParseAmount(text, out _).Should().BeFalse();

    [Theory]
    [InlineData("2026-09-01")]
    [InlineData("01/09/2026")]
    [InlineData("1/9/2026")]
    public void Dates_accept_iso_and_day_first(string text)
    {
        ExpenseCsv.TryParseDate(text, out var date).Should().BeTrue();
        date.Should().Be(new DateOnly(2026, 9, 1));
    }

    [Fact]
    public void Tags_and_currency_are_normalized()
    {
        ExpenseCsv.ParseTags("Infra; infra|Prod").Should().Equal("infra", "prod");
        ExpenseCsv.NormalizeCurrency(null).Should().Be("VND");
        ExpenseCsv.NormalizeCurrency("usd").Should().Be("USD");
        ExpenseCsv.NormalizeCurrency("EUR").Should().BeNull();
    }
}

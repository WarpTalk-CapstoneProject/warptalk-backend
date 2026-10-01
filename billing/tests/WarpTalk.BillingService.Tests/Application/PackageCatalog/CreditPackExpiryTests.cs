using FluentAssertions;
using WarpTalk.BillingService.Application.Services;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.PackageCatalog;

/// <summary>G11: which credits a pack's expiry removes — pack credits are treated as spent first.</summary>
public class CreditPackExpiryTests
{
    [Theory]
    [InlineData(10_000, 0, 50_000, 10_000)] // nothing spent: the whole pack expires
    [InlineData(10_000, 4_000, 50_000, 6_000)] // spent counts against the pack first
    [InlineData(10_000, 12_000, 50_000, 0)] // spent more than the pack: nothing left to expire
    [InlineData(10_000, 0, 3_000, 3_000)] // never more than the balance
    [InlineData(10_000, 0, -200, 0)] // an overdrawn balance loses nothing more
    public void Expired_is_the_unspent_part_of_the_pack_capped_by_the_balance(int pack, long consumed, int balance, int expected) =>
        CreditPackExpiryService.ExpiredCredits(pack, consumed, balance).Should().Be(expected);
}

using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace WarpTalk.BillingService.Tests.Contracts;

/// <summary>
/// The provider costs in 20260918090000_set_provider_cost_on_crd_rate_cards.sql are unit conversions
/// of published prices, and a conversion is only as good as its arithmetic. Each literal is re-derived
/// here in decimal (no floating point) from the price it cites, and the per-character Cartesia prices
/// are cross-checked against the VND rate cards 006 seeded from the same price list — so the two
/// seeds cannot drift apart silently.
/// </summary>
public sealed class CrdProviderCostMigrationContractTests
{
    private const string MigrationFile = "20260918090000_set_provider_cost_on_crd_rate_cards.sql";

    // OpenAI gpt-4o-mini-transcribe, $0.003 per minute of audio.
    private const decimal SttUsdPerMinute = 0.003m;

    // Cartesia Startup plan: $49 for 1,250,000 credits; sonic-3.5 spends 1 credit per character,
    // a voice clone 1.5; Cartesia equates 1,250,000 credits with ~1,667 minutes = 750 characters/min.
    private const decimal CartesiaPlanUsd = 49m;
    private const decimal CartesiaPlanCredits = 1_250_000m;
    private const decimal CloneCreditsPerCharacter = 1.5m;
    private const decimal CharactersPerMinute = 750m;

    [Fact]
    public void Stt_IsThePerMinutePriceDividedBySixty()
    {
        var perSecond = SttUsdPerMinute / 60m;

        perSecond.Should().Be(0.00005m);
        CrdCost("STT").Should().Be(perSecond);
    }

    [Fact]
    public void Dubbing_IsThePerCharacterPriceTimesCharactersPerSecond()
    {
        var perCharacter = CartesiaPlanUsd / CartesiaPlanCredits;
        var clonePerCharacter = perCharacter * CloneCreditsPerCharacter;
        var charactersPerSecond = CharactersPerMinute / 60m;

        perCharacter.Should().Be(0.0000392m);
        clonePerCharacter.Should().Be(0.0000588m);
        charactersPerSecond.Should().Be(12.5m);

        CrdCost("AUDIO_DUBBING_STANDARD").Should().Be(perCharacter * charactersPerSecond).And.Be(0.00049m);
        CrdCost("AUDIO_DUBBING_VOICE_CLONE").Should().Be(clonePerCharacter * charactersPerSecond).And.Be(0.000735m);

        // Same per-character inputs as the VND cards 006 seeded.
        VndCharacterCost("AUDIO_DUBBING_STANDARD").Should().Be(perCharacter);
        VndCharacterCost("AUDIO_DUBBING_VOICE_CLONE").Should().Be(clonePerCharacter);
    }

    [Fact]
    public void OnlyChargeTypesWithARealPriceAreCosted()
    {
        CostedChargeTypes().Should().BeEquivalentTo("STT", "AUDIO_DUBBING_STANDARD", "AUDIO_DUBBING_VOICE_CLONE");
    }

    [Fact]
    public void Migration_LeavesHistoryAndTransactionControlAlone()
    {
        var body = StripComments(ReadMigration(MigrationFile));

        body.Should().NotContain("credit_transactions", "cost is derived at read time; settled rows are never rewritten");
        body.Should().NotContain("usage_records");
        body.Should().NotMatchRegex(@"(?i)\b(BEGIN|COMMIT|ROLLBACK)\b", "the migration runner owns the transaction");
        body.Should().NotMatchRegex(@"(?i)\bunit_price\s*=", "a settled price snapshot must never change");
        // Every write is guarded so a re-run, or a cost an admin has since entered, is left alone.
        Regex.Matches(body, @"(?i)\bUPDATE\b").Count.Should().Be(3);
        Regex.Matches(body, @"(?i)\bunit IS NULL\b").Count.Should().Be(2);
        body.Should().Contain("provider_unit_cost IS NULL");
    }

    private static decimal CrdCost(string chargeType)
    {
        var match = Regex.Match(
            StripComments(ReadMigration(MigrationFile)),
            $@"\('{chargeType}'::varchar,\s*([0-9.]+)::numeric");
        match.Success.Should().BeTrue($"the migration should cost {chargeType}");
        return decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static IEnumerable<string> CostedChargeTypes()
        => Regex.Matches(StripComments(ReadMigration(MigrationFile)), @"\('([A-Z_]+)'::varchar,\s*[0-9.]+::numeric")
            .Select(m => m.Groups[1].Value);

    private static decimal VndCharacterCost(string chargeType)
    {
        var match = Regex.Match(
            ReadMigration("006-seed-phase2-billing-rate-card.sql"),
            $@"\('{chargeType}', 'character', 'VND', 'cartesia', '[^']+',\s*([0-9.]+)::numeric");
        match.Success.Should().BeTrue($"006 should seed a per-character VND card for {chargeType}");
        return decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static string StripComments(string sql) => Regex.Replace(sql, "--[^\n]*", string.Empty);

    private static string ReadMigration(string file)
    {
        var path = Path.Combine(FindBackendRoot(), "billing/database/migrations", file);
        File.Exists(path).Should().BeTrue(path);
        return File.ReadAllText(path);
    }

    private static string FindBackendRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "warptalk-backend.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate backend repository root.");
    }
}

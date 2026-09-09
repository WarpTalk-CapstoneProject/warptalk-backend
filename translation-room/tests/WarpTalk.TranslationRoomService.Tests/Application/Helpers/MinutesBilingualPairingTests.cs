using System.Collections.Generic;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// When a translated line may be printed beside the original it translates.
///
/// This is a signed document, so the failure that matters is not "no pairing" — it is a pairing
/// that is wrong, which attributes a decision to something nobody decided. Every case here is
/// therefore about REFUSING to pair on anything weaker than a shared citation.
/// </summary>
public class MinutesBilingualPairingTests
{
    private static MinutesItem Item(string text, long? atMs) => new() { Text = text, AtMs = atMs };

    [Fact]
    public void ItemsCitingTheSameMomentArePairedRegardlessOfOrder()
    {
        var original = new List<MinutesItem> { Item("Ship on Friday", 2000), Item("Freeze the schema", 1000) };
        var translated = new List<MinutesItem> { Item("Đóng băng schema", 1000), Item("Phát hành thứ Sáu", 2000) };

        var pairs = MinutesBilingualPairing.PairByCitation(original, translated);

        pairs.Should().NotBeNull();
        pairs!.Should().HaveCount(2);
        // Order follows the ORIGINAL, and each line meets the translation citing its own moment —
        // not the one that happened to sit at the same index.
        pairs![0].Original.Text.Should().Be("Ship on Friday");
        pairs![0].Translated.Text.Should().Be("Phát hành thứ Sáu");
        pairs![1].Original.Text.Should().Be("Freeze the schema");
        pairs![1].Translated.Text.Should().Be("Đóng băng schema");
    }

    [Fact]
    public void PositionIsNeverEvidenceOfCorrespondence()
    {
        // Same count, same order, no citations. Index would "work" here and would be a guess.
        var original = new List<MinutesItem> { Item("Ship on Friday", null), Item("Freeze the schema", null) };
        var translated = new List<MinutesItem> { Item("Phát hành thứ Sáu", null), Item("Đóng băng schema", null) };

        MinutesBilingualPairing.PairByCitation(original, translated).Should().BeNull();
    }

    [Fact]
    public void OneUnmatchedOriginalRefusesTheWholeSection()
    {
        // Half-paired sections would print in two layouts at once and leave a reader working out
        // which lines were claims of correspondence.
        var original = new List<MinutesItem> { Item("Ship on Friday", 1000), Item("Freeze the schema", 2000) };
        var translated = new List<MinutesItem> { Item("Phát hành thứ Sáu", 1000) };

        MinutesBilingualPairing.PairByCitation(original, translated).Should().BeNull();
    }

    [Fact]
    public void ARepeatedCitationOnEitherSideIsNotAJoinKey()
    {
        var original = new List<MinutesItem> { Item("A", 1000), Item("B", 1000) };
        var translated = new List<MinutesItem> { Item("A-vi", 1000), Item("B-vi", 1000) };

        // Two lines citing one moment cannot be told apart by it, so neither side may be used.
        MinutesBilingualPairing.PairByCitation(original, translated).Should().BeNull();
    }

    [Fact]
    public void AnOriginalWithNoCitationCannotBePaired()
    {
        var original = new List<MinutesItem> { Item("Ship on Friday", 1000), Item("Freeze the schema", null) };
        var translated = new List<MinutesItem> { Item("Phát hành thứ Sáu", 1000), Item("Đóng băng schema", 2000) };

        MinutesBilingualPairing.PairByCitation(original, translated).Should().BeNull();
    }

    [Fact]
    public void ExtraTranslatedLinesAreToleratedWhenEveryOriginalStillMatches()
    {
        // The model producing one line more than the original does not make the matched lines
        // wrong; only an unmatched ORIGINAL breaks the correspondence.
        var original = new List<MinutesItem> { Item("Ship on Friday", 1000) };
        var translated = new List<MinutesItem> { Item("Phát hành thứ Sáu", 1000), Item("Thừa", 9000) };

        var pairs = MinutesBilingualPairing.PairByCitation(original, translated);

        pairs.Should().NotBeNull();
        pairs!.Should().ContainSingle();
        pairs![0].Translated.Text.Should().Be("Phát hành thứ Sáu");
    }

    [Fact]
    public void NothingToPairAgainstIsNotAPairing()
    {
        var original = new List<MinutesItem> { Item("Ship on Friday", 1000) };

        MinutesBilingualPairing.PairByCitation(original, null).Should().BeNull();
        MinutesBilingualPairing.PairByCitation(original, new List<MinutesItem>()).Should().BeNull();
        MinutesBilingualPairing.PairByCitation(null, original).Should().BeNull();
    }

    [Fact]
    public void SectionsAreMatchedByKeyNotByPosition()
    {
        var section = new MinutesSection { Key = "decisions", Kind = "items" };
        var translated = new List<MinutesSection>
        {
            new() { Key = "actionItems", Kind = "items" },
            new() { Key = "decisions", Kind = "items", Text = "found" }
        };

        MinutesBilingualPairing.CounterpartOf(section, translated)!.Text.Should().Be("found");
    }

    [Fact]
    public void ASectionWithNoTranslatedCounterpartIsNull()
    {
        // A template section the summary worker never translates — "problems", "options" — must
        // read as absent rather than as an empty translation.
        var section = new MinutesSection { Key = "problems", Kind = "items" };
        var translated = new List<MinutesSection> { new() { Key = "decisions", Kind = "items" } };

        MinutesBilingualPairing.CounterpartOf(section, translated).Should().BeNull();
        MinutesBilingualPairing.CounterpartOf(section, null).Should().BeNull();
    }
}

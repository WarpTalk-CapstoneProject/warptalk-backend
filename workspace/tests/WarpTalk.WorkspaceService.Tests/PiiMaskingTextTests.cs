using System.Linq;
using WarpTalk.WorkspaceService.Application.Masking;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// The text half of the masked copy: recovering what the scan hid, and finding it again.
/// </summary>
public class PiiMaskingTextTests
{
    // ---------------------------------------------------------- alignment

    [Fact]
    public void DeriveReplacements_RecoversEachHiddenValue_WithItsMarker()
    {
        var original = "Contact Nguyễn Văn A at van.a@example.com or 0912345678 today.";
        var masked = "Contact [PII_REDACTED] at [EMAIL_REDACTED] or [PHONE_REDACTED] today.";

        var found = PiiMaskAlignment.DeriveReplacements(original, masked)!;

        Assert.Equal(3, found.Count);
        Assert.Contains(found, r => r.Value == "Nguyễn Văn A" && r.Placeholder == "[PII_REDACTED]");
        Assert.Contains(found, r => r.Value == "van.a@example.com" && r.Placeholder == "[EMAIL_REDACTED]");
        Assert.Contains(found, r => r.Value == "0912345678" && r.Placeholder == "[PHONE_REDACTED]");
    }

    [Fact]
    public void DeriveReplacements_HandlesAMarkerAtTheStartAndAtTheEnd()
    {
        var found = PiiMaskAlignment.DeriveReplacements(
            "Trần Thị B signed for 079198001234",
            "[PII_REDACTED] signed for [ID_REDACTED]")!;

        Assert.Equal(new[] { "079198001234", "Trần Thị B" }, found.Select(r => r.Value).OrderBy(v => v));
    }

    [Fact]
    public void DeriveReplacements_IgnoresWhitespaceTheModelChanged()
    {
        var original = "Name:\tLê Văn C\r\n\r\nPhone:   0987654321\r\nEnd of record";
        var masked = "Name: [PII_REDACTED]\nPhone: [PHONE_REDACTED]\nEnd of record";

        var found = PiiMaskAlignment.DeriveReplacements(original, masked)!;

        Assert.Contains(found, r => r.Value == "Lê Văn C");
        Assert.Contains(found, r => r.Value == "0987654321");
    }

    [Fact]
    public void DeriveReplacements_SurvivesAnEditFarFromAnyMarker()
    {
        var filler = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor sit amet", 20));
        var original = $"We will will receive the parcel. {filler} Call 0912345678 now.";
        // The model dropped a doubled word it was not asked to touch, so everything after it
        // sits a few characters earlier than an exact echo would put it.
        var masked = $"We will receive the parcel. {filler} Call [PHONE_REDACTED] now.";

        var found = PiiMaskAlignment.DeriveReplacements(original, masked)!;

        Assert.Equal("0912345678", Assert.Single(found).Value);
    }

    [Fact]
    public void DeriveReplacements_SplitsAValueThatSpansLines_SoEachParagraphIsHiddenOnItsOwn()
    {
        var original = "Address:\n12 Nguyễn Huệ\nQuận 1, TP.HCM\nThanks";
        var masked = "Address:\n[PII_REDACTED]\nThanks";

        var found = PiiMaskAlignment.DeriveReplacements(original, masked)!;

        Assert.Contains(found, r => r.Value == "12 Nguyễn Huệ");
        Assert.Contains(found, r => r.Value == "Quận 1, TP.HCM");
    }

    [Fact]
    public void DeriveReplacements_AdjacentMarkers_CoverTheWholeHole()
    {
        var found = PiiMaskAlignment.DeriveReplacements(
            "Owner: Phạm D0912345678 (mobile)",
            "Owner: [PII_REDACTED][PHONE_REDACTED] (mobile)")!;

        var only = Assert.Single(found);
        Assert.Equal("Phạm D0912345678", only.Value);
        Assert.Equal(PiiMaskAlignment.GenericPlaceholder, only.Placeholder);
    }

    [Fact]
    public void DeriveReplacements_ReturnsNull_WhenTheTextsDoNotLineUp()
    {
        Assert.Null(PiiMaskAlignment.DeriveReplacements(
            "The quarterly report is attached for Hoàng E.",
            "A completely different sentence about [PII_REDACTED] and nothing else."));
    }

    [Fact]
    public void DeriveReplacements_ReturnsNull_WhenAHoleIsTooWideToBeOnePersonalDetail()
    {
        var huge = string.Concat(Enumerable.Repeat("x", PiiMaskAlignment.MaxValueLength + 50));
        Assert.Null(PiiMaskAlignment.DeriveReplacements($"Start {huge} end of text", "Start [PII_REDACTED] end of text"));
    }

    [Fact]
    public void DeriveReplacements_NoMarkers_IsAnEmptyList_NotAFailure()
    {
        Assert.Empty(PiiMaskAlignment.DeriveReplacements("nothing here", "nothing here")!);
    }

    [Fact]
    public void DeriveReplacements_AMarkerAlreadyInTheDocument_IsNotAValue()
    {
        var found = PiiMaskAlignment.DeriveReplacements(
            "Old export: [PHONE_REDACTED]. New: 0912345678.",
            "Old export: [PHONE_REDACTED]. New: [PHONE_REDACTED].")!;

        Assert.Equal("0912345678", Assert.Single(found).Value);
    }

    [Fact]
    public void PiiReplacement_ToString_DoesNotPrintTheValue()
    {
        var text = new PiiReplacement("0912345678", "[PHONE_REDACTED]").ToString();
        Assert.DoesNotContain("0912345678", text);
    }

    // ------------------------------------------------------------ matcher

    [Fact]
    public void Matcher_ReplacesAValue_WhereverItStandsAlone()
    {
        var matcher = new PiiValueMatcher(new[] { new PiiReplacement("An", "[PII_REDACTED]") });

        Assert.Equal("[PII_REDACTED] met Andrew and [PII_REDACTED].", matcher.Replace("An met Andrew and An."));
        Assert.False(matcher.ContainsAny("Andrew and Anna"));
    }

    [Fact]
    public void Matcher_PrefersTheLongerValue()
    {
        var matcher = new PiiValueMatcher(new[]
        {
            new PiiReplacement("0912", "[ID_REDACTED]"),
            new PiiReplacement("0912 345 678", "[PHONE_REDACTED]")
        });

        Assert.Equal("Call [PHONE_REDACTED].", matcher.Replace("Call 0912 345 678."));
    }

    [Fact]
    public void Matcher_FindsADecomposedSpellingOfAComposedValue()
    {
        var composed = "Nguyễn";
        var decomposed = composed.Normalize(System.Text.NormalizationForm.FormD);
        Assert.NotEqual(composed, decomposed);

        var matcher = new PiiValueMatcher(new[] { new PiiReplacement(composed, "[PII_REDACTED]") });

        Assert.Equal("Dear [PII_REDACTED],", matcher.Replace($"Dear {decomposed},"));
    }

    // -------------------------------------------------------------- regex

    [Theory]
    [InlineData("mail me at someone@example.com", 1)]
    [InlineData("phone 0912345678", 1)]
    [InlineData("phone +84 912 345 678", 1)]
    [InlineData("CCCD 079198001234", 1)]
    [InlineData("card 4111 1111 1111 1111", 1)]
    [InlineData("order 4111 1111 1111 1112", 0)]
    [InlineData("[EMAIL_REDACTED] and [PHONE_REDACTED], invoice 2024-0001", 0)]
    public void RegexScanner_CountsWhatTheWorkersFastPathWouldMask(string text, int expected)
    {
        Assert.Equal(expected, PiiRegexScanner.CountFindings(text));
    }
}

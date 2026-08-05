using WarpTalk.TranslationRoomService.Application.Helpers;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// Pins the half of the language contract that lives in code: every value the service handles
/// is folded to the bare ISO-639-1 code. The other half — the catalog is keyed by locale tags —
/// lives in SQL, and the two disagreeing is what broke room creation in production. See
/// LanguageRepositoryTests for the side that proves the lookup tolerates both.
/// </summary>
public class LanguageHelperTests
{
    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("vi-VN", "vi")]
    [InlineData("ja-JP", "ja")]
    [InlineData("ko-KR", "ko")]
    [InlineData("zh-CN", "zh")]
    public void NormalizeLanguageCode_DropsTheRegionSubtag(string input, string expected)
    {
        // This is why an exact match against the locale-tagged catalog could never hit:
        // TranslationRoomService and LanguagePolicy both run values through here first.
        Assert.Equal(expected, LanguageHelper.NormalizeLanguageCode(input));
    }

    [Theory]
    [InlineData("EN", "en")]
    [InlineData("  vi-VN  ", "vi")]
    [InlineData("vi", "vi")]
    public void NormalizeLanguageCode_TrimsAndLowercases(string input, string expected)
    {
        Assert.Equal(expected, LanguageHelper.NormalizeLanguageCode(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeLanguageCode_ReturnsEmptyForNothing(string? input)
    {
        Assert.Equal(string.Empty, LanguageHelper.NormalizeLanguageCode(input));
    }

    [Fact]
    public void NormalizeLanguageCode_ReturnsEmptyForABareSeparator()
    {
        // Guards the catalog lookup: an empty primary subtag used as a LIKE prefix would match
        // every language on file.
        Assert.Equal(string.Empty, LanguageHelper.NormalizeLanguageCode("-"));
    }

    [Fact]
    public void SerializeTargetLanguages_StoresBareCodes()
    {
        // Rooms persist the normalized form, which is what the AI pipeline is keyed by.
        var serialized = LanguageHelper.SerializeTargetLanguages(["en-US", "vi-VN"]);

        Assert.Contains("\"en\"", serialized);
        Assert.Contains("\"vi\"", serialized);
        Assert.DoesNotContain("en-US", serialized);
    }

    [Fact]
    public void ParseTargetLanguages_ReadsBackTheBareCodes()
    {
        var parsed = LanguageHelper.ParseTargetLanguages("[\"en-US\",\"vi-VN\"]");

        Assert.Equal(["en", "vi"], parsed);
    }
}

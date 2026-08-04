using WarpTalk.WorkspaceService.Application.Helpers;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

public class SearchTextHelperTests
{
    [Theory]
    [InlineData("Trần Mạnh Tuấn", "manh")]      // WT-231: the reported case
    [InlineData("Trần Mạnh Tuấn", "MANH")]
    [InlineData("Trần Mạnh Tuấn", "tran manh")]
    [InlineData("Huỳnh Thái Tú", "huynh")]
    [InlineData("Ngô Xuân Hạnh Nhi", "hanh nhi")]
    [InlineData("Đặng Văn A", "dang")]          // đ does not decompose under NFD
    [InlineData("Đặng Văn A", "Đặng")]          // an accented term still matches
    public void Matches_IgnoresDiacriticsAndCase(string value, string term)
    {
        Assert.True(SearchTextHelper.Matches(value, term));
    }

    [Theory]
    [InlineData("Trần Mạnh Tuấn", "nhi")]
    [InlineData("Huỳnh Ngọc Kỳ", "manh")]
    [InlineData("Unknown", "manh")]
    public void Matches_RejectsNonMatchingTerms(string value, string term)
    {
        Assert.False(SearchTextHelper.Matches(value, term));
    }

    [Theory]
    [InlineData("manh.tuan@warptalk.io.vn", "manh")]
    [InlineData("manh.tuan@warptalk.io.vn", "WARPTALK")]
    public void Matches_WorksOnEmails(string value, string term)
    {
        Assert.True(SearchTextHelper.Matches(value, term));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Matches_EmptyTermMatchesEverything(string? term)
    {
        Assert.True(SearchTextHelper.Matches("Trần Mạnh Tuấn", term));
    }

    [Fact]
    public void Matches_EmptyValueOnlyMatchesEmptyTerm()
    {
        Assert.False(SearchTextHelper.Matches(null, "manh"));
        Assert.True(SearchTextHelper.Matches(null, ""));
    }

    [Fact]
    public void Fold_StripsDiacriticsAndLowercases()
    {
        Assert.Equal("tran manh tuan", SearchTextHelper.Fold("  Trần Mạnh Tuấn  "));
        Assert.Equal("dang", SearchTextHelper.Fold("Đặng"));
        Assert.Equal(string.Empty, SearchTextHelper.Fold(null));
    }
}

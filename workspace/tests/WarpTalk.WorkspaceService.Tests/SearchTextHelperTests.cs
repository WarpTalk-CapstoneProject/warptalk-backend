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

    [Theory]
    // The reported miss: a document named BUG-TRACKING-WT478-494, searched for the way a person
    // says it. WarpBot answered "no document whose name contains bug tracking" while the file
    // was one row away in the same list.
    [InlineData("BUG-TRACKING-WT478-494", "bug tracking")]
    [InlineData("BUG-TRACKING-WT478-494", "BUG TRACKING")]
    [InlineData("BUG-TRACKING-WT478-494", "bug-tracking")]
    [InlineData("BUG-TRACKING-WT478-494.md", "tracking wt478")]
    [InlineData("refactor_workspace_identity.md", "workspace identity")]
    [InlineData("Report4_Software Design Document.docx", "software design")]
    // And the reverse: punctuation typed against a name that has none.
    [InlineData("Database Design Document", "database-design")]
    public void Matches_IgnoresSeparators(string value, string term)
    {
        Assert.True(SearchTextHelper.Matches(value, term));
    }

    [Theory]
    // Folding separators must not fold words together: "bugtracking" is not what the file says.
    [InlineData("BUG-TRACKING-WT478-494", "bugtracking")]
    [InlineData("BUG-TRACKING-WT478-494", "tracking bug")]
    public void Matches_DoesNotJoinWordsAcrossSeparators(string value, string term)
    {
        Assert.False(SearchTextHelper.Matches(value, term));
    }

    [Fact]
    public void Fold_CollapsesRunsOfSeparatorsIntoOneSpace()
    {
        Assert.Equal("bug tracking wt478 494", SearchTextHelper.Fold("BUG-TRACKING-WT478-494"));
        Assert.Equal("wt478 494", SearchTextHelper.Fold("WT478 -  494"));
        // Leading and trailing punctuation carries no word to separate, so it leaves no space.
        Assert.Equal("readme", SearchTextHelper.Fold("--readme--"));
    }

    [Fact]
    public void Fold_StripsDiacriticsAndLowercases()
    {
        Assert.Equal("tran manh tuan", SearchTextHelper.Fold("  Trần Mạnh Tuấn  "));
        Assert.Equal("dang", SearchTextHelper.Fold("Đặng"));
        Assert.Equal(string.Empty, SearchTextHelper.Fold(null));
    }
}

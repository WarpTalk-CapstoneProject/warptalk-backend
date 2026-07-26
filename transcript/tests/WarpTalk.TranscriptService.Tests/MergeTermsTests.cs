using System.Collections.Generic;
using WarpTalk.TranscriptService.Infrastructure.Redis;
using Xunit;

namespace WarpTalk.TranscriptService.Tests;

using PromptTerm = GlossaryStartedEventConsumer.PromptTerm;

public class MergeTermsTests
{
    [Fact]
    public void MergeTerms_WorkspaceTermAlwaysWinsOverGlobalTermWithSameKey()
    {
        var workspaceTerms = new List<PromptTerm> { new("architect", "kiến trúc sư", 5) };
        var globalTerms = new List<PromptTerm> { new("architect", "architect", 8) };

        var (merged, droppedAsOverridden, droppedAsOverBudget) =
            GlossaryStartedEventConsumer.MergeTerms(workspaceTerms, globalTerms, maxTerms: 60);

        var term = Assert.Single(merged);
        Assert.Equal("kiến trúc sư", term.Target);
        Assert.Equal(1, droppedAsOverridden);
        Assert.Equal(0, droppedAsOverBudget);
    }

    [Fact]
    public void MergeTerms_OverrideMatchIsCaseAndWhitespaceInsensitive()
    {
        var workspaceTerms = new List<PromptTerm> { new("  Sprint  ", "sờ-prin", 5) };
        var globalTerms = new List<PromptTerm> { new("sprint", "sprint", 8) };

        var (merged, droppedAsOverridden, _) =
            GlossaryStartedEventConsumer.MergeTerms(workspaceTerms, globalTerms, maxTerms: 60);

        Assert.Single(merged);
        Assert.Equal(1, droppedAsOverridden);
    }

    [Fact]
    public void MergeTerms_KeepsBothTermsWhenKeysDiffer()
    {
        var workspaceTerms = new List<PromptTerm> { new("ARR", "doanh thu định kỳ", 5) };
        var globalTerms = new List<PromptTerm> { new("architect", "architect", 8) };

        var (merged, droppedAsOverridden, droppedAsOverBudget) =
            GlossaryStartedEventConsumer.MergeTerms(workspaceTerms, globalTerms, maxTerms: 60);

        Assert.Equal(2, merged.Count);
        Assert.Equal(0, droppedAsOverridden);
        Assert.Equal(0, droppedAsOverBudget);
    }

    [Fact]
    public void MergeTerms_WorkspaceTermsFillBudgetFirstThenGlobalTermsFillTheRest()
    {
        var workspaceTerms = new List<PromptTerm>
        {
            new("term-a", "a", 1),
            new("term-b", "b", 1),
        };
        var globalTerms = new List<PromptTerm>
        {
            new("term-c", "c", 9),
            new("term-d", "d", 8),
            new("term-e", "e", 7),
        };

        var (merged, droppedAsOverridden, droppedAsOverBudget) =
            GlossaryStartedEventConsumer.MergeTerms(workspaceTerms, globalTerms, maxTerms: 3);

        Assert.Equal(3, merged.Count);
        Assert.Equal(new[] { "term-a", "term-b", "term-c" }, merged.ConvertAll(t => t.Source));
        Assert.Equal(0, droppedAsOverridden);
        Assert.Equal(2, droppedAsOverBudget);
    }

    [Fact]
    public void MergeTerms_GlobalTermsOrderedByPriorityDescendingWhenFillingBudget()
    {
        var workspaceTerms = new List<PromptTerm>();
        var globalTerms = new List<PromptTerm>
        {
            new("low", "low", 1),
            new("high", "high", 9),
            new("mid", "mid", 5),
        };

        var (merged, _, _) = GlossaryStartedEventConsumer.MergeTerms(workspaceTerms, globalTerms, maxTerms: 60);

        Assert.Equal(new[] { "high", "mid", "low" }, merged.ConvertAll(t => t.Source));
    }

    [Fact]
    public void MergeTerms_EmptyInputsReturnEmptyMerge()
    {
        var (merged, droppedAsOverridden, droppedAsOverBudget) =
            GlossaryStartedEventConsumer.MergeTerms(new List<PromptTerm>(), new List<PromptTerm>(), maxTerms: 60);

        Assert.Empty(merged);
        Assert.Equal(0, droppedAsOverridden);
        Assert.Equal(0, droppedAsOverBudget);
    }

    [Theory]
    [InlineData("architect", "architect")]
    [InlineData("  architect  ", "architect")]
    [InlineData("Architect", "architect")]
    [InlineData("full  stack", "full stack")]
    public void NormalizeKey_TrimsLowercasesAndCollapsesWhitespace(string input, string expected)
    {
        Assert.Equal(expected, GlossaryStartedEventConsumer.NormalizeKey(input));
    }
}

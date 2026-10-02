using System.Collections.Generic;
using WarpTalk.WorkspaceService.Application.Evaluators;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// The whole table for "which version of a document does this caller get".
/// </summary>
/// <remarks>
/// The function is pure, so the tests can afford to be exhaustive: the last one walks every
/// combination of the seven inputs against an independent restatement of the product decisions,
/// and the named ones pin each decision by itself so a failure says which rule moved.
/// </remarks>
public class DocumentContentAccessDecisionTests
{
    private static DocumentContentAccessInput Input(
        bool ownerOrAdmin = false,
        bool uploader = false,
        bool restricted = true,
        bool granted = false,
        bool grantedIgnoringRestriction = true,
        bool masked = false,
        bool byPii = false) =>
        new(ownerOrAdmin, uploader, restricted, granted, grantedIgnoringRestriction, masked, byPii);

    // ---- documents that are not restricted behave exactly as today

    [Theory]
    [InlineData(true, DocumentContentVersion.Original)]
    [InlineData(false, DocumentContentVersion.None)]
    public void NotRestricted_FollowsTheEvaluator_AndNothingElse(bool granted, DocumentContentVersion expected)
    {
        foreach (var ownerOrAdmin in new[] { true, false })
        foreach (var uploader in new[] { true, false })
        foreach (var masked in new[] { true, false })
        foreach (var byPii in new[] { true, false })
        {
            var input = Input(ownerOrAdmin, uploader, restricted: false, granted, granted, masked, byPii);
            Assert.Equal(expected, DocumentContentAccessDecision.Decide(input));
            Assert.False(DocumentContentAccessDecision.CanReadMaskedVersion(input));
        }
    }

    // ---- member

    [Fact]
    public void Member_RestrictedWithMaskedCopy_GetsMasked()
    {
        var input = Input(masked: true, byPii: true);
        Assert.Equal(DocumentContentVersion.Masked, DocumentContentAccessDecision.Decide(input));
        Assert.True(DocumentContentAccessDecision.CanReadMaskedVersion(input));
        Assert.True(DocumentContentAccessDecision.CanOpenDocument(input));
    }

    [Fact]
    public void Member_HoldingAnAllowPolicy_StillGetsOnlyMasked()
    {
        var input = Input(granted: true, masked: true, byPii: true);
        Assert.Equal(DocumentContentVersion.Masked, DocumentContentAccessDecision.Decide(input));
    }

    [Fact]
    public void Member_PiiRestrictedWithNoMaskedCopy_GetsNothing_ButMayOpenThePage()
    {
        var input = Input(masked: false, byPii: true);
        Assert.Equal(DocumentContentVersion.None, DocumentContentAccessDecision.Decide(input));
        Assert.False(DocumentContentAccessDecision.CanReadMaskedVersion(input));
        Assert.True(DocumentContentAccessDecision.CanOpenDocument(input));
    }

    [Fact]
    public void Member_HoldingAnAllowPolicy_PiiRestrictedWithNoMaskedCopy_GetsNothing_NeverTheOriginal()
    {
        var input = Input(granted: true, masked: false, byPii: true);
        Assert.Equal(DocumentContentVersion.None, DocumentContentAccessDecision.Decide(input));
    }

    [Fact]
    public void Member_RestrictedForAnotherReason_StaysBlocked_AndCannotOpenThePage()
    {
        // DLP keyword, failed scan, or a label set by hand: there is no masked copy to show.
        var input = Input(masked: false, byPii: false);
        Assert.Equal(DocumentContentVersion.None, DocumentContentAccessDecision.Decide(input));
        Assert.False(DocumentContentAccessDecision.CanOpenDocument(input));
    }

    [Fact]
    public void Member_HoldingAnAllowPolicy_RestrictedForAnotherReason_KeepsTheOriginal_AsToday()
    {
        var input = Input(granted: true, masked: false, byPii: false);
        Assert.Equal(DocumentContentVersion.Original, DocumentContentAccessDecision.Decide(input));
    }

    [Fact]
    public void Member_WhoWouldBeRefusedEvenIfNotRestricted_GetsNothing_EvenWithAMaskedCopy()
    {
        // An explicit DENY, a private document, an External member outside a meeting.
        var input = Input(grantedIgnoringRestriction: false, masked: true, byPii: true);
        Assert.Equal(DocumentContentVersion.None, DocumentContentAccessDecision.Decide(input));
        Assert.False(DocumentContentAccessDecision.CanReadMaskedVersion(input));
        Assert.False(DocumentContentAccessDecision.CanOpenDocument(input));
    }

    // ---- Owner/Admin and the uploader

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void OwnerAdminAndUploader_GetTheOriginal_AndMayReadTheMaskedCopyToo(bool ownerOrAdmin, bool uploader)
    {
        var withMasked = Input(ownerOrAdmin, uploader, granted: true, masked: true, byPii: true);
        Assert.Equal(DocumentContentVersion.Original, DocumentContentAccessDecision.Decide(withMasked));
        Assert.True(DocumentContentAccessDecision.CanReadMaskedVersion(withMasked));

        var withoutMasked = Input(ownerOrAdmin, uploader, granted: true, masked: false, byPii: true);
        Assert.Equal(DocumentContentVersion.Original, DocumentContentAccessDecision.Decide(withoutMasked));
        Assert.False(DocumentContentAccessDecision.CanReadMaskedVersion(withoutMasked));
    }

    [Fact]
    public void Uploader_DeniedByAnExplicitPolicy_GetsNothing_NotEvenTheMaskedCopy()
    {
        var input = Input(uploader: true, granted: false, grantedIgnoringRestriction: false, masked: true, byPii: true);
        Assert.Equal(DocumentContentVersion.None, DocumentContentAccessDecision.Decide(input));
        Assert.False(DocumentContentAccessDecision.CanReadMaskedVersion(input));
        Assert.False(DocumentContentAccessDecision.CanOpenDocument(input));
    }

    // ---- the whole table

    public static IEnumerable<object[]> EveryCombination()
    {
        var flags = new[] { false, true };
        foreach (var ownerOrAdmin in flags)
        foreach (var uploader in flags)
        foreach (var restricted in flags)
        foreach (var granted in flags)
        foreach (var ignoring in flags)
        foreach (var masked in flags)
        foreach (var byPii in flags)
        {
            yield return new object[] { ownerOrAdmin, uploader, restricted, granted, ignoring, masked, byPii };
        }
    }

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public void EveryCombination_MatchesTheProductDecisions(
        bool ownerOrAdmin, bool uploader, bool restricted, bool granted, bool ignoring, bool masked, bool byPii)
    {
        var input = new DocumentContentAccessInput(ownerOrAdmin, uploader, restricted, granted, ignoring, masked, byPii);
        var version = DocumentContentAccessDecision.Decide(input);

        // The decisions, restated independently of the implementation's branch order.
        var privileged = ownerOrAdmin || uploader;
        var passesOtherGates = granted || ignoring;

        DocumentContentVersion expected;
        if (!restricted || privileged)
        {
            expected = granted ? DocumentContentVersion.Original : DocumentContentVersion.None;
        }
        else if (masked)
        {
            expected = passesOtherGates ? DocumentContentVersion.Masked : DocumentContentVersion.None;
        }
        else if (byPii)
        {
            expected = DocumentContentVersion.None;
        }
        else
        {
            expected = granted ? DocumentContentVersion.Original : DocumentContentVersion.None;
        }

        Assert.Equal(expected, version);

        // Invariants that hold for every row.
        if (restricted && !privileged && (masked || byPii))
        {
            // A member never receives the original of a document restricted for personal details.
            Assert.NotEqual(DocumentContentVersion.Original, version);
        }

        if (version == DocumentContentVersion.Masked)
        {
            Assert.True(restricted && masked && !privileged);
        }

        if (!masked)
        {
            Assert.NotEqual(DocumentContentVersion.Masked, version);
            Assert.False(DocumentContentAccessDecision.CanReadMaskedVersion(input));
        }

        if (version != DocumentContentVersion.None)
        {
            Assert.True(DocumentContentAccessDecision.CanOpenDocument(input));
        }

        if (!granted && !ignoring)
        {
            Assert.Equal(DocumentContentVersion.None, version);
            Assert.False(DocumentContentAccessDecision.CanOpenDocument(input));
        }
    }

    [Fact]
    public void WireValues_AreTheThreeTheWebReads()
    {
        Assert.Equal("original", DocumentContentVersion.Original.ToWireValue());
        Assert.Equal("masked", DocumentContentVersion.Masked.ToWireValue());
        Assert.Equal("none", DocumentContentVersion.None.ToWireValue());
    }
}

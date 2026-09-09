using System;
using System.Linq;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// Who a share link lets in.
///
/// This is the rule the whole sharing feature rests on: get it wrong and a company's minutes are
/// on the open internet, or the person they were sent to cannot read them. Every case is pinned
/// here, without a database, because that is the only way the rule stays readable.
/// </summary>
public class MinutesShareAccessTests
{
    private static readonly DateTime Now = new(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc);

    private static MeetingMinutesShareLink Link(
        string mode = MeetingMinutesConstants.ShareModeInvitedOnly,
        DateTime? expiresAt = null,
        DateTime? revokedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        Token = MinutesShareAccess.NewToken(),
        AccessMode = mode,
        AllowDownload = true,
        ExpiresAt = expiresAt,
        RevokedAt = revokedAt
    };

    [Fact]
    public void APublicLinkOpensForSomebodyWithNoAccountAtAll()
    {
        MinutesShareAccess.Decide(
                Link(MeetingMinutesConstants.ShareModeAnyoneWithLink),
                viewerSignedIn: false,
                viewerMayRead: false,
                Now)
            .Should().Be(ShareDecision.Granted);
    }

    [Fact]
    public void ARestrictedLinkAsksAnAnonymousReaderToSignInRatherThanRefusingThem()
    {
        // An invited person clicking their own link has done nothing wrong — they are just not
        // signed in yet, and a 404 there is a support ticket.
        MinutesShareAccess.Decide(Link(), viewerSignedIn: false, viewerMayRead: false, Now)
            .Should().Be(ShareDecision.SignInRequired);
    }

    [Fact]
    public void ARestrictedLinkRefusesASignedInStranger()
    {
        MinutesShareAccess.Decide(Link(), viewerSignedIn: true, viewerMayRead: false, Now)
            .Should().Be(ShareDecision.Forbidden);
    }

    [Fact]
    public void ARestrictedLinkOpensForSomebodyOnTheList()
    {
        MinutesShareAccess.Decide(Link(), viewerSignedIn: true, viewerMayRead: true, Now)
            .Should().Be(ShareDecision.Granted);
    }

    [Fact]
    public void ARevokedLinkIsIndistinguishableFromOneThatNeverExisted()
    {
        // Distinguishing them would turn the endpoint into an oracle for which documents exist.
        var revoked = MinutesShareAccess.Decide(
            Link(revokedAt: Now.AddMinutes(-1)), viewerSignedIn: true, viewerMayRead: true, Now);
        var missing = MinutesShareAccess.Decide(null, viewerSignedIn: true, viewerMayRead: true, Now);

        revoked.Should().Be(ShareDecision.NoSuchLink);
        missing.Should().Be(ShareDecision.NoSuchLink);
    }

    [Fact]
    public void RevocationBeatsEvenAPublicMode()
    {
        MinutesShareAccess.Decide(
                Link(MeetingMinutesConstants.ShareModeAnyoneWithLink, revokedAt: Now.AddDays(-1)),
                viewerSignedIn: false,
                viewerMayRead: false,
                Now)
            .Should().Be(ShareDecision.NoSuchLink);
    }

    [Fact]
    public void AnExpiredLinkIsClosedAndOneExpiringLaterIsNot()
    {
        MinutesShareAccess.Decide(
                Link(MeetingMinutesConstants.ShareModeAnyoneWithLink, expiresAt: Now.AddSeconds(-1)),
                viewerSignedIn: false, viewerMayRead: false, Now)
            .Should().Be(ShareDecision.NoSuchLink);

        MinutesShareAccess.Decide(
                Link(MeetingMinutesConstants.ShareModeAnyoneWithLink, expiresAt: Now.AddDays(7)),
                viewerSignedIn: false, viewerMayRead: false, Now)
            .Should().Be(ShareDecision.Granted);
    }

    [Fact]
    public void AnUnknownModeIsTreatedAsRestrictedRatherThanOpen()
    {
        // The safe direction to fail. A typo'd mode must never be the reason a document went
        // public.
        MinutesShareAccess.Decide(
                Link("ANYONE"), viewerSignedIn: false, viewerMayRead: false, Now)
            .Should().Be(ShareDecision.SignInRequired);

        MinutesShareAccess.IsKnownMode("ANYONE").Should().BeFalse();
        MinutesShareAccess.IsKnownMode(MeetingMinutesConstants.ShareModeAnyoneWithLink).Should().BeTrue();
        MinutesShareAccess.IsKnownMode(MeetingMinutesConstants.ShareModeInvitedOnly).Should().BeTrue();
    }

    [Fact]
    public void TokensAreUrlSafeUnguessableAndNeverRepeated()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => MinutesShareAccess.NewToken()).ToList();

        tokens.Should().OnlyHaveUniqueItems();
        tokens.Should().OnlyContain(token => token.Length >= 40);
        // Nothing that changes meaning when pasted into a URL.
        tokens.Should().OnlyContain(token =>
            token.All(character =>
                char.IsLetterOrDigit(character) || character == '-' || character == '_'));
    }
}

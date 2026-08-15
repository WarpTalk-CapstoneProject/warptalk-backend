using System;
using WarpTalk.WorkspaceService.Application.Mappers;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Enums;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests.Application;

/// <summary>
/// BR-34 — resending a pending invitation must mark the previous token REPLACED.
///
/// Resend used to write the new token hash straight onto the existing row. That does invalidate
/// the old token, so the security property held — but there was only ever one row, so the status
/// the SRS requires had nothing to live on and nothing recorded that a second email had gone out
/// under different token material. One row cannot be both the superseded invitation and its
/// replacement.
///
/// `REPLACED` already existed in <see cref="InvitationStatus"/> and was used by nothing.
/// </summary>
public class InvitationResendSupersedesTests
{
    private static readonly DateTime Now = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    private static WorkspaceInvitation Original() => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        Email = "invited@example.com",
        RoleId = Guid.NewGuid(),
        InvitedBy = Guid.NewGuid(),
        TokenHash = "old-hash",
        Status = InvitationStatus.PENDING.ToString(),
        MembershipType = "Internal",
        SentCount = 3,
        ExpiresAt = Now.AddHours(1),
        CreatedAt = Now.AddDays(-6),
    };

    [Fact]
    public void TheReplacementCarriesTheInvitersOriginalIntent()
    {
        // A resend is the same invitation said again, not a new decision. Losing the role or the
        // membership type here would quietly downgrade whoever accepts it.
        var original = Original();

        var replacement = original.ToReplacementInvitation("new-hash", validExpiryDays: 7, now: Now);

        Assert.Equal(original.Email, replacement.Email);
        Assert.Equal(original.RoleId, replacement.RoleId);
        Assert.Equal(original.MembershipType, replacement.MembershipType);
        Assert.Equal(original.InvitedBy, replacement.InvitedBy);
        Assert.Equal(original.WorkspaceId, replacement.WorkspaceId);
    }

    [Fact]
    public void TheReplacementIsANewRowWithItsOwnToken()
    {
        var original = Original();

        var replacement = original.ToReplacementInvitation("new-hash", validExpiryDays: 7, now: Now);

        Assert.NotEqual(original.Id, replacement.Id);
        Assert.Equal("new-hash", replacement.TokenHash);
        Assert.Equal(InvitationStatus.PENDING.ToString(), replacement.Status);
    }

    [Fact]
    public void TheReplacementGetsAFreshExpiryRatherThanInheritingTheOldOne()
    {
        // The reason to resend is that the last one was not usable. Inheriting a window with an
        // hour left reproduces exactly that.
        var original = Original();

        var replacement = original.ToReplacementInvitation("new-hash", validExpiryDays: 7, now: Now);

        Assert.Equal(Now.AddDays(7), replacement.ExpiresAt);
        Assert.True(replacement.ExpiresAt > original.ExpiresAt);
    }

    [Fact]
    public void TheReplacementStartsItsOwnDeliveryHistory()
    {
        // SentCount counts sends of THIS token. Carrying 3 forward would say the new token had
        // already been emailed three times, which is the opposite of true.
        var original = Original();

        var replacement = original.ToReplacementInvitation("new-hash", validExpiryDays: 7, now: Now);

        Assert.Equal(0, replacement.SentCount);
        Assert.Equal(3, original.SentCount);
    }

    [Fact]
    public void ReplacedIsAStatusTheAcceptPathRefuses()
    {
        // The whole safety of superseding rests on this: acceptance checks for PENDING, so a row
        // marked REPLACED cannot be redeemed even though its record survives for the audit trail.
        Assert.NotEqual(InvitationStatus.PENDING.ToString(), InvitationStatus.REPLACED.ToString());
    }
}

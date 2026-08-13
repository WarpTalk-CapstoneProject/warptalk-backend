using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;

namespace WarpTalk.AuthService.Tests;

/// <summary>
/// Consent to voice cloning, kept as a record rather than a flag.
///
/// The table is append-only, so these pin the two things that follow from that: the current
/// answer is the newest row (not "the GRANTED one", which would find a grant that has since been
/// withdrawn), and a repeated click does not manufacture decisions the person never made.
/// </summary>
public class VoiceConsentServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 13, 10, 0, 0, DateTimeKind.Utc);

    private readonly IVoiceConsentRepository _repository = Substitute.For<IVoiceConsentRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly VoiceConsentService _service;
    private readonly List<VoiceConsent> _added = new();

    public VoiceConsentServiceTests()
    {
        _unitOfWork.VoiceConsentRepository.Returns(_repository);
        _repository.When(r => r.Add(Arg.Any<VoiceConsent>()))
            .Do(call => _added.Add(call.Arg<VoiceConsent>()));

        _service = new VoiceConsentService(
            _unitOfWork, NullLogger<VoiceConsentService>.Instance, () => Now);
    }

    private void CurrentIs(VoiceConsent? consent) =>
        _repository
            .GetCurrentAsync(Arg.Any<Guid>(), VoiceConsentTypes.VoiceClone, Arg.Any<CancellationToken>())
            .Returns(consent);

    private static VoiceConsent Row(string status) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        ConsentType = VoiceConsentTypes.VoiceClone,
        ConsentStatus = status,
        ConsentTextVersion = "2026-01-01.v0",
        GrantedAt = status == VoiceConsentStatuses.Granted ? Now.AddDays(-1) : null,
        CreatedAt = Now.AddDays(-1),
    };

    [Fact]
    public async Task NeverAskedIsNotTheSameAsSayingNo()
    {
        CurrentIs(null);

        var status = (await _service.GetStatusAsync(Guid.NewGuid())).Value!;

        // A UI shows the consent prompt to the first and must not nag the second.
        Assert.False(status.HasDecided);
        Assert.False(status.IsGranted);
    }

    [Fact]
    public async Task GrantingRecordsTheWordingThatWasAgreedTo()
    {
        CurrentIs(null);

        await _service.GrantAsync(Guid.NewGuid(), new VoiceConsentDecisionContext("1.2.3.4", "Firefox"));

        var row = Assert.Single(_added);
        Assert.Equal(VoiceConsentStatuses.Granted, row.ConsentStatus);
        Assert.Equal(VoiceConsentTextVersions.Current, row.ConsentTextVersion);
        Assert.Equal(Now, row.GrantedAt);
        Assert.Equal("1.2.3.4", row.IpAddress);
    }

    [Fact]
    public async Task GrantingTwiceDoesNotInventASecondDecision()
    {
        CurrentIs(Row(VoiceConsentStatuses.Granted));

        await _service.GrantAsync(Guid.NewGuid(), default);

        // A double-click, a retry or a second tab must not litter an audit trail.
        Assert.Empty(_added);
    }

    [Fact]
    public async Task RevokingKeepsTheVersionOfTheGrantItEnds()
    {
        var grant = Row(VoiceConsentStatuses.Granted);
        CurrentIs(grant);

        await _service.RevokeAsync(grant.UserId, new VoiceConsentDecisionContext("5.6.7.8", "Safari"));

        var row = Assert.Single(_added);
        Assert.Equal(VoiceConsentStatuses.Revoked, row.ConsentStatus);
        Assert.Equal(Now, row.RevokedAt);
        // This row ends THAT agreement, so it carries that agreement's wording, not today's.
        Assert.Equal(grant.ConsentTextVersion, row.ConsentTextVersion);
        Assert.Equal(grant.GrantedAt, row.GrantedAt);
    }

    [Fact]
    public async Task RevokingSomethingNeverGrantedRecordsNothing()
    {
        CurrentIs(null);

        var result = await _service.RevokeAsync(Guid.NewGuid(), default);

        Assert.True(result.IsSuccess);
        // Writing a revocation here would put a decision in the trail the person never made.
        Assert.Empty(_added);
    }

    [Theory]
    [InlineData(VoiceConsentStatuses.Granted, true)]
    [InlineData(VoiceConsentStatuses.Revoked, false)]
    [InlineData(VoiceConsentStatuses.Expired, false)]
    public async Task OnlyALiveGrantCountsAsConsent(string status, bool expected)
    {
        CurrentIs(Row(status));

        Assert.Equal(expected, await _service.HasActiveConsentAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task NoRecordMeansNoConsent()
    {
        CurrentIs(null);

        // The default in front of biometric processing has to be refusal.
        Assert.False(await _service.HasActiveConsentAsync(Guid.NewGuid()));
    }
}

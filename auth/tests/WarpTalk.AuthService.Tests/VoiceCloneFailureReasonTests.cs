using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Clients;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests;

/// <summary>
/// "Couldn't clone", 2026-09-18.
///
/// Every upload from that day failed at the voice provider: the Cartesia account had dropped to the
/// Free plan, and Cartesia answers a clone there with HTTP 402 plan_upgrade_required. The profile
/// row recorded only status = clone_failed. The reason was in one log line on each side, and the
/// next deploy deleted both — so the page showed a failure nobody could explain, and offered
/// Re-record, which could not have helped.
///
/// These pin the two halves of the fix: the reason is STORED and shown, and a failed clone can be
/// retried from the stored recording without a new take.
/// </summary>
public class VoiceCloneFailureReasonTests
{
    private static readonly byte[] Recording = { 0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3 };

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IVoiceProfileRepository _profiles = Substitute.For<IVoiceProfileRepository>();
    private readonly IVoiceSampleRepository _samples = Substitute.For<IVoiceSampleRepository>();
    private readonly IVoiceConsentRepository _consents = Substitute.For<IVoiceConsentRepository>();
    private readonly IVoiceSampleStorage _storage = Substitute.For<IVoiceSampleStorage>();
    private readonly IVoiceCloneRequestQueue _queue = Substitute.For<IVoiceCloneRequestQueue>();
    private readonly VoiceProfileService _service;

    private readonly Guid _userId = Guid.NewGuid();
    private readonly VoiceProfile _profile;

    public VoiceCloneFailureReasonTests()
    {
        _profile = new VoiceProfile
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            DisplayName = "tu huynh",
            Language = "vi-VN",
            Status = "active",
            IsActive = true,
        };

        _unitOfWork.VoiceProfileRepository.Returns(_profiles);
        _unitOfWork.VoiceSampleRepository.Returns(_samples);
        _unitOfWork.VoiceConsentRepository.Returns(_consents);
        _profiles.GetByUserIdAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new List<VoiceProfile> { _profile });
        _storage.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(Recording)));
        _queue.RequestAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(true);
        ConsentIs("GRANTED");
        StoredSample();

        _service = new VoiceProfileService(
            _unitOfWork,
            _storage,
            Substitute.For<IVoiceCatalogDirectory>(),
            _queue,
            Substitute.For<IVoicePreviewQueue>(),
            Substitute.For<ILogger<VoiceProfileService>>());
    }

    private void OutcomeIs(VoiceCloneOutcome? outcome) =>
        _queue.TakeOutcomeAsync(_profile.Id, Arg.Any<CancellationToken>()).Returns(outcome);

    private void ConsentIs(string? status, DateTime? revokedAt = null) =>
        _consents.GetCurrentAsync(_userId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(status is null
                ? null
                : new VoiceConsent
                {
                    Id = Guid.NewGuid(),
                    UserId = _userId,
                    ConsentType = "VOICE_PROFILE_UPLOAD",
                    ConsentStatus = status,
                    ConsentTextVersion = "v1",
                    GrantedAt = DateTime.UtcNow,
                    RevokedAt = revokedAt,
                });

    private void StoredSample(bool present = true) =>
        _samples.FindAsync(
                Arg.Any<Expression<Func<VoiceSample, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(present
                ? new List<VoiceSample>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        VoiceProfileId = _profile.Id,
                        FileUrl = $"{_userId}/{_profile.Id}.webm",
                        ContainsRawAudio = true,
                        CreatedAt = DateTime.UtcNow,
                    },
                }
                : new List<VoiceSample>());

    private void Failed(string? code = "PROVIDER_PLAN_REQUIRED", string? error = "plan")
    {
        _profile.Status = "clone_failed";
        _profile.IsActive = false;
        _profile.CloneErrorCode = code;
        _profile.CloneError = error;
    }

    // ── the reason is stored, and it reaches the page ─────────────────────────────────────────

    [Fact]
    public async Task AFailedCloneKeepsItsReasonAndThePageIsGivenIt()
    {
        OutcomeIs(new VoiceCloneOutcome(
            null, "cartesia",
            "the voice provider account's plan does not include voice cloning",
            "PROVIDER_PLAN_REQUIRED"));

        var result = await _service.GetProfilesAsync(_userId);

        Assert.Equal("clone_failed", _profile.Status);
        Assert.Equal("PROVIDER_PLAN_REQUIRED", _profile.CloneErrorCode);
        Assert.Contains("plan", _profile.CloneError);
        var dto = Assert.Single(result.Value!);
        Assert.Equal("PROVIDER_PLAN_REQUIRED", dto.CloneErrorCode);
        Assert.Contains("plan", dto.CloneError);
    }

    [Fact]
    public async Task AnAnswerFromAnOlderWorkerWithNoCodeIsStoredAsUnknownNotDropped()
    {
        OutcomeIs(new VoiceCloneOutcome(null, "cartesia", "Error code: 500"));

        await _service.GetProfilesAsync(_userId);

        Assert.Equal("UNKNOWN", _profile.CloneErrorCode);
        Assert.Equal("Error code: 500", _profile.CloneError);
    }

    [Fact]
    public async Task AnOverlongDetailIsCutToTheColumnNotAllowedToFailTheSave()
    {
        OutcomeIs(new VoiceCloneOutcome(null, null, new string('x', 2_000), "SAMPLE_REJECTED"));

        await _service.GetProfilesAsync(_userId);

        Assert.Equal(500, _profile.CloneError!.Length);
    }

    [Fact]
    public async Task ASuccessfulCloneClearsAnEarlierFailureReason()
    {
        _profile.CloneErrorCode = "PROVIDER_PLAN_REQUIRED";
        _profile.CloneError = "plan";
        OutcomeIs(new VoiceCloneOutcome("cartesia-voice-abc", "cartesia", null));

        var result = await _service.GetProfilesAsync(_userId);

        Assert.Null(_profile.CloneErrorCode);
        Assert.Null(_profile.CloneError);
        Assert.Null(Assert.Single(result.Value!).CloneErrorCode);
    }

    [Fact]
    public async Task TheWorkersCamelCasePayloadIsReadWithItsCode()
    {
        // The exact bytes warptalk-ai writes. A field name that does not bind would store every
        // failure as UNKNOWN and the fix would be decoration.
        const string payload =
            "{\"voiceId\": null, \"provider\": \"cartesia\", " +
            "\"error\": \"the voice provider account's plan does not include voice cloning\", " +
            "\"errorCode\": \"PROVIDER_PLAN_REQUIRED\"}";
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult((RedisValue)payload));
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        var queue = new RedisVoiceCloneRequestQueue(
            redis, Substitute.For<ILogger<RedisVoiceCloneRequestQueue>>());

        var outcome = await queue.TakeOutcomeAsync(_profile.Id);

        Assert.NotNull(outcome);
        Assert.Null(outcome!.VoiceId);
        Assert.Equal("PROVIDER_PLAN_REQUIRED", outcome.ErrorCode);
    }

    // ── a failed clone can be retried from the stored recording ───────────────────────────────

    [Fact]
    public async Task RetrySendsTheStoredRecordingBackAndReturnsTheRowToCloning()
    {
        Failed();

        var result = await _service.RetryCloneAsync(_userId, _profile.Id);

        Assert.True(result.IsSuccess, result.Error);
        await _queue.Received(1).RequestAsync(
            _profile.Id, _userId, "vi-VN",
            Arg.Is<byte[]>(b => b.Length == Recording.Length), Arg.Any<CancellationToken>());
        Assert.Equal("active", _profile.Status);
        Assert.True(_profile.IsActive);
        Assert.Null(_profile.CloneErrorCode);
        Assert.Null(_profile.CloneError);
        Assert.Null(result.Value!.ProviderVoiceId);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryWorksForARowThatFailedBeforeReasonsWereStored()
    {
        // "tu huynh" and "ian" in production: clone_failed with no recorded reason.
        Failed(code: null, error: null);

        var result = await _service.RetryCloneAsync(_userId, _profile.Id);

        Assert.True(result.IsSuccess, result.Error);
    }

    [Fact]
    public async Task RetryRefusesAProfileThatHasNotFailed()
    {
        // A pending clone is already queued and a finished one has a voice; either way a second
        // paid provider call would buy nothing.
        var result = await _service.RetryCloneAsync(_userId, _profile.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidState, result.ErrorCode);
        await _queue.DidNotReceiveWithAnyArgs().RequestAsync(default, default, default!, default!, default);
    }

    [Fact]
    public async Task RetryOfSomebodyElsesProfileIsNotFound()
    {
        Failed();

        var result = await _service.RetryCloneAsync(Guid.NewGuid(), _profile.Id);

        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    [Theory]
    [InlineData(null, false)]        // never given
    [InlineData("REVOKED", false)]   // withdrawn
    [InlineData("GRANTED", true)]    // granted once, withdrawn since
    public async Task RetryNeedsTheUploadConsentStillInForce(string? status, bool revoked)
    {
        Failed();
        ConsentIs(status, revoked ? DateTime.UtcNow : null);

        var result = await _service.RetryCloneAsync(_userId, _profile.Id);

        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        await _queue.DidNotReceiveWithAnyArgs().RequestAsync(default, default, default!, default!, default);
    }

    [Fact]
    public async Task RetryWithNoStoredRecordingAsksForANewTake()
    {
        Failed();
        StoredSample(present: false);

        var result = await _service.RetryCloneAsync(_userId, _profile.Id);

        Assert.Equal(ErrorCodes.InvalidState, result.ErrorCode);
        Assert.Contains("Record it again", result.Error);
    }

    [Fact]
    public async Task RetryThatCannotBeQueuedLeavesTheFailureVisible()
    {
        Failed();
        _queue.RequestAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _service.RetryCloneAsync(_userId, _profile.Id);

        Assert.Equal(ErrorCodes.ServiceUnavailable, result.ErrorCode);
        Assert.Equal("clone_failed", _profile.Status);
        Assert.Equal("PROVIDER_PLAN_REQUIRED", _profile.CloneErrorCode);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}

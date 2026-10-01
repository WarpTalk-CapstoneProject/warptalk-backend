using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Helpers;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests;

/// <summary>
/// WT-888 — a voice profile is made only from a live recording that reads a server-issued phrase.
///
/// The five consent checkboxes were self-attested, and the dialog accepted any audio file, so
/// cloning somebody from an old recording of them took nothing but ticking boxes. These pin the
/// check that replaced that: a phrase is issued per language, expires, is good for exactly one
/// attempt, and a recording passes only if it was heard to say it.
/// </summary>
public class VoiceEnrollmentChallengeTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly Guid _userId = Guid.NewGuid();
    private readonly InMemoryChallenges _challenges = new();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ISpeechTranscriber _transcriber = Substitute.For<ISpeechTranscriber>();
    private DateTime _clock = Now;
    private readonly VoiceEnrollmentChallengeService _service;

    public VoiceEnrollmentChallengeTests()
    {
        _unitOfWork.VoiceEnrollmentChallengeRepository.Returns(_challenges);
        _service = new VoiceEnrollmentChallengeService(
            _unitOfWork,
            _transcriber,
            Substitute.For<ILogger<VoiceEnrollmentChallengeService>>(),
            () => _clock);
    }

    private void Hears(string transcript) =>
        _transcriber.TranscribeAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(Result.Success(transcript));

    private Task<Result<VoiceEnrollmentVerification>> Verify(Guid? challengeId, string language = "en-US", Guid? profileId = null) =>
        _service.VerifyAsync(
            _userId, challengeId, language, new byte[] { 1, 2, 3 }, "voice-sample.webm", "audio/webm;codecs=opus",
            profileId ?? Guid.NewGuid());

    // ── Issue ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("en-US", "en", 8)]
    [InlineData("vi-VN", "vi", 7)]
    [InlineData("ja-JP", "ja", 6)]
    public async Task IssuesAPhraseInTheProfilesLanguage_WithAnExpiry(string language, string phraseLanguage, int words)
    {
        var result = await _service.IssueAsync(_userId, language);

        Assert.True(result.IsSuccess, result.Error);
        var dto = result.Value!;
        Assert.Equal(phraseLanguage, dto.Language);
        Assert.Equal(Now + VoiceEnrollmentChallengeService.ChallengeLifetime, dto.ExpiresAt);
        var separator = phraseLanguage == "ja" ? "、" : " ";
        var expectedTokens = phraseLanguage == "vi" ? words * 2 : words;
        Assert.Equal(expectedTokens, dto.Phrase.Split(separator).Length);

        var stored = Assert.Single(_challenges.Rows);
        Assert.Equal(_userId, stored.UserId);
        Assert.Equal(dto.Phrase, stored.Phrase);
        Assert.Null(stored.ConsumedAt);
    }

    [Fact]
    public async Task TwoPhrasesAreNotTheSame()
    {
        var first = await _service.IssueAsync(_userId, "en-US");
        var second = await _service.IssueAsync(_userId, "en-US");

        Assert.NotEqual(first.Value!.Phrase, second.Value!.Phrase);
        Assert.NotEqual(first.Value.ChallengeId, second.Value.ChallengeId);
    }

    [Fact]
    public async Task IssuingIsCappedPerAccount()
    {
        for (var i = 0; i < VoiceEnrollmentChallengeService.MaxIssuedPerWindow; i++)
        {
            Assert.True((await _service.IssueAsync(_userId, "en")).IsSuccess);
        }

        var refused = await _service.IssueAsync(_userId, "en");

        Assert.False(refused.IsSuccess);
        Assert.Equal(ErrorCodes.RateLimitExceeded, refused.ErrorCode);
    }

    // ── Verify: the phrase must be heard ────────────────────────────────────────────────────

    [Fact]
    public async Task ARecordingThatSaysThePhrasePasses_AndRecordsTheEvidence()
    {
        var phrase = (await _service.IssueAsync(_userId, "en-US")).Value!;
        Hears($"Okay, here goes. {phrase.Phrase}. Thank you!");
        var profileId = Guid.NewGuid();

        var result = await Verify(phrase.ChallengeId, profileId: profileId);

        Assert.True(result.IsSuccess, result.Error);
        var row = _challenges.Rows.Single();
        Assert.Equal(VoiceEnrollmentOutcomes.Passed, row.Outcome);
        Assert.Equal(Now, row.ConsumedAt);
        Assert.Equal(profileId, row.VoiceProfileId);
        Assert.True(row.MatchScore >= 0.95m);
        Assert.Contains("here goes", row.Transcript);
    }

    [Fact]
    public async Task ARecordingThatSaysSomethingElseIsRefused()
    {
        var phrase = (await _service.IssueAsync(_userId, "en-US")).Value!;
        Hears("Hi everyone, welcome to the quarterly planning meeting, let's get started.");

        var result = await Verify(phrase.ChallengeId);

        Assert.False(result.IsSuccess);
        Assert.Equal(VoiceEnrollmentErrorCodes.ChallengeMismatch, result.ErrorCode);
        var row = _challenges.Rows.Single();
        Assert.Equal(VoiceEnrollmentOutcomes.Mismatch, row.Outcome);
        Assert.Null(row.VoiceProfileId);
        Assert.NotNull(row.ConsumedAt);
    }

    [Fact]
    public async Task AVietnamesePhraseHeardWithAWrongToneStillPasses()
    {
        var phrase = (await _service.IssueAsync(_userId, "vi-VN")).Value!;
        // A transcriber that drops every tone mark — the syllables are right, the tones are not.
        Hears(VoiceChallengeMatcher.Normalize(phrase.Phrase));

        var result = await Verify(phrase.ChallengeId, "vi-VN");

        Assert.True(result.IsSuccess, result.Error);
    }

    [Fact]
    public async Task ARecordingIsCheckedInThePhrasesLanguage_WithoutThePhraseAsAPrompt()
    {
        var phrase = (await _service.IssueAsync(_userId, "vi-VN")).Value!;
        Hears(phrase.Phrase);

        await Verify(phrase.ChallengeId, "vi-VN");

        await _transcriber.Received(1).TranscribeAsync(
            Arg.Any<byte[]>(), "voice-sample.webm", "audio/webm;codecs=opus", "vi", Arg.Any<CancellationToken>());
    }

    // ── Verify: single use, expiry, ownership ──────────────────────────────────────────────

    [Fact]
    public async Task APhraseIsGoodForOneAttempt_EvenWhenThatAttemptPassed()
    {
        var phrase = (await _service.IssueAsync(_userId, "en-US")).Value!;
        Hears(phrase.Phrase);
        Assert.True((await Verify(phrase.ChallengeId)).IsSuccess);

        var replay = await Verify(phrase.ChallengeId);

        Assert.False(replay.IsSuccess);
        Assert.Equal(VoiceEnrollmentErrorCodes.ChallengeInvalid, replay.ErrorCode);
        await _transcriber.ReceivedWithAnyArgs(1).TranscribeAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task AFailedAttemptAlsoUsesThePhraseUp()
    {
        var phrase = (await _service.IssueAsync(_userId, "en-US")).Value!;
        Hears("something else entirely");
        Assert.False((await Verify(phrase.ChallengeId)).IsSuccess);

        Hears(phrase.Phrase);
        var retry = await Verify(phrase.ChallengeId);

        Assert.Equal(VoiceEnrollmentErrorCodes.ChallengeInvalid, retry.ErrorCode);
    }

    [Fact]
    public async Task AnExpiredPhraseIsRefused_WithoutPayingForATranscription()
    {
        var phrase = (await _service.IssueAsync(_userId, "en-US")).Value!;
        Hears(phrase.Phrase);
        _clock = Now + VoiceEnrollmentChallengeService.ChallengeLifetime + TimeSpan.FromSeconds(1);

        var result = await Verify(phrase.ChallengeId);

        Assert.False(result.IsSuccess);
        Assert.Equal(VoiceEnrollmentErrorCodes.ChallengeInvalid, result.ErrorCode);
        Assert.Contains("expired", result.Error!, StringComparison.OrdinalIgnoreCase);
        await _transcriber.DidNotReceiveWithAnyArgs().TranscribeAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task SomebodyElsesPhraseIsRefused()
    {
        var theirs = (await _service.IssueAsync(Guid.NewGuid(), "en-US")).Value!;
        Hears(theirs.Phrase);

        var result = await Verify(theirs.ChallengeId);

        Assert.Equal(VoiceEnrollmentErrorCodes.ChallengeInvalid, result.ErrorCode);
        Assert.Null(_challenges.Rows.Single().ConsumedAt);
    }

    [Fact]
    public async Task APhraseIssuedForAnotherLanguageIsRefused()
    {
        var english = (await _service.IssueAsync(_userId, "en-US")).Value!;
        Hears(english.Phrase);

        var result = await Verify(english.ChallengeId, "vi-VN");

        Assert.Equal(VoiceEnrollmentErrorCodes.ChallengeInvalid, result.ErrorCode);
    }

    [Fact]
    public async Task NoChallengeAtAllIsRefused()
    {
        var result = await Verify(null);

        Assert.Equal(VoiceEnrollmentErrorCodes.ChallengeRequired, result.ErrorCode);
        await _transcriber.DidNotReceiveWithAnyArgs().TranscribeAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task ARecordingThatCouldNotBeTranscribedIsUnavailable_NotAMismatch()
    {
        var phrase = (await _service.IssueAsync(_userId, "en-US")).Value!;
        _transcriber.TranscribeAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(Result.Failure<string>("down", ErrorCodes.ServiceUnavailable));

        var result = await Verify(phrase.ChallengeId);

        Assert.Equal(ErrorCodes.ServiceUnavailable, result.ErrorCode);
        Assert.Equal(VoiceEnrollmentOutcomes.TranscriptionFailed, _challenges.Rows.Single().Outcome);
    }

    // ── The match rule itself ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("apple river window garden yellow orange mountain pencil",
        "Apple, river, window, garden, yellow, orange, mountain, pencil.", true)]
    [InlineData("apple river window garden yellow orange mountain pencil",
        "apple river windows garden yellow orange mountains pencil", true)]
    [InlineData("apple river window garden yellow orange mountain pencil",
        "um so apple river window garden yellow orange mountain pencil and that's it", true)]
    [InlineData("apple river window garden yellow orange mountain pencil",
        "apple river window", false)]
    [InlineData("apple river window garden yellow orange mountain pencil",
        "the weather today is lovely and I am going for a walk", false)]
    [InlineData("apple river window garden yellow orange mountain pencil", "", false)]
    [InlineData("con mèo quả cam dòng sông", "Con mèo, quả cam, dòng sông.", true)]
    [InlineData("con mèo quả cam dòng sông", "con meo qua cam dong song", true)]
    [InlineData("con mèo quả cam dòng sông", "xin chào mọi người hôm nay", false)]
    [InlineData("テレビ、カメラ、ピアノ、バナナ", "テレビ カメラ ピアノ バナナ。", true)]
    [InlineData("テレビ、カメラ、ピアノ、バナナ", "こんにちは、よろしくお願いします", false)]
    public void TheMatchRule(string phrase, string transcript, bool passes)
    {
        var similarity = VoiceChallengeMatcher.Similarity(phrase, transcript);

        Assert.Equal(passes, VoiceChallengeMatcher.Passes(similarity));
    }

    /// <summary>
    /// The repository's guarantee, as this service relies on it: one claim per challenge, only
    /// while unconsumed, unexpired and owned. The real one is a single conditional UPDATE — see
    /// VoiceEnrollmentChallengeRepositoryTests for it against PostgreSQL.
    /// </summary>
    private sealed class InMemoryChallenges : IVoiceEnrollmentChallengeRepository
    {
        public List<VoiceEnrollmentChallenge> Rows { get; } = new();

        public Task<VoiceEnrollmentChallenge?> GetForUserAsync(Guid id, Guid userId, CancellationToken ct = default) =>
            Task.FromResult(Rows.FirstOrDefault(c => c.Id == id && c.UserId == userId));

        public Task<bool> TryConsumeAsync(Guid id, Guid userId, DateTime now, CancellationToken ct = default)
        {
            var row = Rows.FirstOrDefault(c => c.Id == id && c.UserId == userId && c.ConsumedAt == null && c.ExpiresAt > now);
            if (row == null) return Task.FromResult(false);
            row.ConsumedAt = now;
            return Task.FromResult(true);
        }

        public Task<int> CountIssuedSinceAsync(Guid userId, DateTime since, CancellationToken ct = default) =>
            Task.FromResult(Rows.Count(c => c.UserId == userId && c.CreatedAt >= since));

        public Task AddAsync(VoiceEnrollmentChallenge entity, CancellationToken ct = default)
        {
            Rows.Add(entity);
            return Task.CompletedTask;
        }

        public Task<VoiceEnrollmentChallenge?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Rows.FirstOrDefault(c => c.Id == id));

        public Task<IReadOnlyList<VoiceEnrollmentChallenge>> GetAllAsync(string includeProperties = "", CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<VoiceEnrollmentChallenge>>(Rows.ToList());

        public Task<IReadOnlyList<VoiceEnrollmentChallenge>> FindAsync(
            Expression<Func<VoiceEnrollmentChallenge, bool>> predicate, string includeProperties = "", CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<VoiceEnrollmentChallenge>>(Rows.Where(predicate.Compile()).ToList());

        public Task<VoiceEnrollmentChallenge?> FirstOrDefaultAsync(
            Expression<Func<VoiceEnrollmentChallenge, bool>> predicate, string includeProperties = "", CancellationToken ct = default) =>
            Task.FromResult(Rows.FirstOrDefault(predicate.Compile()));

        public Task<bool> AnyAsync(Expression<Func<VoiceEnrollmentChallenge, bool>> predicate, CancellationToken ct = default) =>
            Task.FromResult(Rows.Any(predicate.Compile()));

        public void Update(VoiceEnrollmentChallenge entity)
        {
        }

        public void Remove(VoiceEnrollmentChallenge entity) => Rows.Remove(entity);
    }
}

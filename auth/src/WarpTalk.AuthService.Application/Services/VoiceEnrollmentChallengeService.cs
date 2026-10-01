using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Helpers;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Services;

/// <summary>
/// WT-888 — see <see cref="IVoiceEnrollmentChallengeService"/>. The phrase bank and the match
/// rule (and its documented threshold) live in <see cref="VoiceChallengePhrases"/> and
/// <see cref="VoiceChallengeMatcher"/>.
/// </summary>
public class VoiceEnrollmentChallengeService : IVoiceEnrollmentChallengeService
{
    /// <summary>Long enough to read the phrase a few times over; short enough to be "now".</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Phrases one account may be issued per <see cref="IssueWindow"/>. Every phrase can buy one
    /// paid transcription, so this is also the cap on what one account can spend retrying.
    /// </summary>
    public const int MaxIssuedPerWindow = 10;

    public static readonly TimeSpan IssueWindow = TimeSpan.FromMinutes(10);

    private const int TranscriptMaxLength = 1000;

    private readonly IUnitOfWork _unitOfWork;
    private readonly ISpeechTranscriber _transcriber;
    private readonly ILogger<VoiceEnrollmentChallengeService> _logger;
    private readonly Func<DateTime> _clock;

    public VoiceEnrollmentChallengeService(
        IUnitOfWork unitOfWork,
        ISpeechTranscriber transcriber,
        ILogger<VoiceEnrollmentChallengeService> logger)
        : this(unitOfWork, transcriber, logger, () => DateTime.UtcNow)
    {
    }

    /// <summary>Test seam: the clock expiry is judged against.</summary>
    public VoiceEnrollmentChallengeService(
        IUnitOfWork unitOfWork,
        ISpeechTranscriber transcriber,
        ILogger<VoiceEnrollmentChallengeService> logger,
        Func<DateTime> clock)
    {
        _unitOfWork = unitOfWork;
        _transcriber = transcriber;
        _logger = logger;
        _clock = clock;
    }

    public async Task<Result<VoiceEnrollmentChallengeDto>> IssueAsync(
        Guid userId, string language, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return Result.Failure<VoiceEnrollmentChallengeDto>("Language is required.", ErrorCodes.ValidationError);
        }

        var now = _clock();
        var recent = await _unitOfWork.VoiceEnrollmentChallengeRepository
            .CountIssuedSinceAsync(userId, now - IssueWindow, ct);
        if (recent >= MaxIssuedPerWindow)
        {
            return Result.Failure<VoiceEnrollmentChallengeDto>(
                "Too many recording attempts. Wait a few minutes and try again.",
                ErrorCodes.RateLimitExceeded);
        }

        var phraseLanguage = VoiceChallengePhrases.PhraseLanguageFor(language);
        var challenge = new VoiceEnrollmentChallenge
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Language = phraseLanguage,
            Phrase = VoiceChallengePhrases.Generate(phraseLanguage),
            ExpiresAt = now + ChallengeLifetime,
            CreatedAt = now,
        };

        await _unitOfWork.VoiceEnrollmentChallengeRepository.AddAsync(challenge, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new VoiceEnrollmentChallengeDto(
            challenge.Id, challenge.Language, challenge.Phrase, challenge.ExpiresAt));
    }

    public async Task<Result<VoiceEnrollmentVerification>> VerifyAsync(
        Guid userId,
        Guid? challengeId,
        string language,
        byte[] audio,
        string fileName,
        string contentType,
        Guid voiceProfileId,
        CancellationToken ct = default)
    {
        if (challengeId is not { } id || id == Guid.Empty)
        {
            return Result.Failure<VoiceEnrollmentVerification>(
                "Record your voice in the app, reading the phrase shown. Uploaded audio files are not accepted.",
                VoiceEnrollmentErrorCodes.ChallengeRequired);
        }

        var repository = _unitOfWork.VoiceEnrollmentChallengeRepository;
        var challenge = await repository.GetForUserAsync(id, userId, ct);
        if (challenge == null)
        {
            return Invalid("This recording phrase is not valid. Start a new recording.");
        }

        if (!string.Equals(challenge.Language, VoiceChallengePhrases.PhraseLanguageFor(language), StringComparison.Ordinal))
        {
            return Invalid("This recording phrase was issued for a different language. Start a new recording.");
        }

        var now = _clock();
        if (challenge.ConsumedAt != null)
        {
            return Invalid("This recording phrase has already been used. Start a new recording.");
        }

        if (challenge.ExpiresAt <= now)
        {
            return Invalid("This recording phrase has expired. Start a new recording.");
        }

        // The atomic claim. The checks above give a clear reason; this is the guarantee — two
        // uploads racing with one id cannot both get past it.
        if (!await repository.TryConsumeAsync(id, userId, now, ct))
        {
            return Invalid("This recording phrase has already been used or has expired. Start a new recording.");
        }

        challenge.ConsumedAt = now;

        var transcription = await _transcriber.TranscribeAsync(audio, fileName, contentType, challenge.Language, ct);
        if (!transcription.IsSuccess)
        {
            challenge.Outcome = VoiceEnrollmentOutcomes.TranscriptionFailed;
            await SaveQuietlyAsync(challenge.Id, ct);
            return Result.Failure<VoiceEnrollmentVerification>(
                "We couldn't check your recording right now. Please try again in a moment.",
                ErrorCodes.ServiceUnavailable);
        }

        var transcript = transcription.Value ?? string.Empty;
        var similarity = VoiceChallengeMatcher.Similarity(challenge.Phrase, transcript);
        var passed = VoiceChallengeMatcher.Passes(similarity);

        challenge.Transcript = transcript.Length > TranscriptMaxLength ? transcript[..TranscriptMaxLength] : transcript;
        challenge.MatchScore = Math.Round((decimal)similarity, 3);
        challenge.Outcome = passed ? VoiceEnrollmentOutcomes.Passed : VoiceEnrollmentOutcomes.Mismatch;
        if (passed)
        {
            challenge.VoiceProfileId = voiceProfileId;
        }
        await SaveQuietlyAsync(challenge.Id, ct);

        _logger.LogInformation(
            "Voice enrollment challenge {ChallengeId} checked: {Outcome} (similarity {Similarity:F3}, threshold {Threshold}).",
            challenge.Id, challenge.Outcome, similarity, VoiceChallengeMatcher.PassThreshold);

        if (!passed)
        {
            return Result.Failure<VoiceEnrollmentVerification>(
                "Your recording didn't match the phrase on screen. Read every word as shown, clearly, and try again.",
                VoiceEnrollmentErrorCodes.ChallengeMismatch);
        }

        return Result.Success(new VoiceEnrollmentVerification(transcript, similarity));
    }

    private static Result<VoiceEnrollmentVerification> Invalid(string message) =>
        Result.Failure<VoiceEnrollmentVerification>(message, VoiceEnrollmentErrorCodes.ChallengeInvalid);

    /// <summary>
    /// The outcome is evidence, not the decision. The challenge is already consumed by the atomic
    /// claim, so failing to write the transcript must not turn a verdict into a 500.
    /// </summary>
    private async Task SaveQuietlyAsync(Guid challengeId, CancellationToken ct)
    {
        try
        {
            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record the outcome of voice enrollment challenge {ChallengeId}.", challengeId);
        }
    }
}

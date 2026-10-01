using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Interfaces;

/// <summary>
/// WT-888 — the read-aloud check that stands between a recording and a voice profile.
///
/// Issue a random phrase in the profile's language; later, check that a recording says it. The
/// phrase is single-use and short-lived, so the only practical way to produce a passing clip is to
/// speak it, live, when asked — which an old recording of somebody else cannot do.
/// </summary>
public interface IVoiceEnrollmentChallengeService
{
    Task<Result<VoiceEnrollmentChallengeDto>> IssueAsync(
        Guid userId, string language, CancellationToken ct = default);

    /// <summary>
    /// Consume <paramref name="challengeId"/> and check that <paramref name="audio"/> says its
    /// phrase. Consumed whichever way it goes. On success the challenge row records
    /// <paramref name="voiceProfileId"/> as the profile the recording became.
    /// </summary>
    Task<Result<VoiceEnrollmentVerification>> VerifyAsync(
        Guid userId,
        Guid? challengeId,
        string language,
        byte[] audio,
        string fileName,
        string contentType,
        Guid voiceProfileId,
        CancellationToken ct = default);
}

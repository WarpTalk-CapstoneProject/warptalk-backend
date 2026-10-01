using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

public interface IVoiceEnrollmentChallengeRepository : IGenericRepository<VoiceEnrollmentChallenge>
{
    /// <summary>This user's challenge, tracked; null when it does not exist or is someone else's.</summary>
    Task<VoiceEnrollmentChallenge?> GetForUserAsync(Guid id, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Claim the challenge for one attempt: true for exactly one caller, and only while it is
    /// unconsumed and unexpired at <paramref name="now"/>.
    ///
    /// A single conditional UPDATE rather than read-then-write, so two uploads racing with the
    /// same id cannot both pass the "not yet used" check. Writes immediately — it does not wait
    /// for SaveChanges, and it does not update a tracked copy of the row.
    /// </summary>
    Task<bool> TryConsumeAsync(Guid id, Guid userId, DateTime now, CancellationToken ct = default);

    /// <summary>How many challenges this user has been issued since <paramref name="since"/>.</summary>
    Task<int> CountIssuedSinceAsync(Guid userId, DateTime since, CancellationToken ct = default);
}

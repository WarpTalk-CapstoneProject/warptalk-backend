using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

public interface IVoiceProfileRepository : IGenericRepository<VoiceProfile>
{
    Task<IReadOnlyList<VoiceProfile>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task<VoiceProfile?> GetByIdForUserAsync(Guid id, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// This person's automatically-captured voice for one language, if they have one (WT-B).
    ///
    /// Scoped to <see cref="Domain.Constants.VoiceProfileSources.InMeeting"/> deliberately. An
    /// uploaded recording is something somebody deliberately made and must never be overwritten
    /// by a capture, so the upsert has to be able to find one kind without finding the other.
    /// </summary>
    Task<VoiceProfile?> GetAutoCloneAsync(Guid userId, string language, CancellationToken ct = default);

    /// <summary>
    /// Every automatically-captured voice this person has, best likeness first.
    ///
    /// Ordered by score with unscored rows LAST, then most recent. Unscored means nobody measured
    /// it, not that it is bad — but a measured good one is a better answer than an unknown one,
    /// and something has to break the tie deterministically or the voice a person is dubbed in
    /// would wander between meetings.
    /// </summary>
    Task<IReadOnlyList<VoiceProfile>> GetAutoClonesAsync(Guid userId, CancellationToken ct = default);

    void Add(VoiceProfile entity);
}

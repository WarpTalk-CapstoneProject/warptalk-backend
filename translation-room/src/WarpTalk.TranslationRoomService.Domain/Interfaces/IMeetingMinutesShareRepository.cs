using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

/// <summary>
/// The share link of a room's biên bản and the people invited to it.
///
/// One repository for both tables because they are one decision: "who can open this document".
/// Splitting them would let a caller read the mode without the list it depends on.
/// </summary>
public interface IMeetingMinutesShareRepository
{
    /// <summary>The room's sharing state, or null when it has never been shared.</summary>
    Task<MeetingMinutesShareLink?> GetByRoomIdAsync(Guid roomId, CancellationToken ct = default);

    /// <summary>
    /// The link a URL names. Null for an unknown token — indistinguishable, deliberately, from a
    /// link that was revoked and re-issued.
    /// </summary>
    Task<MeetingMinutesShareLink?> GetByTokenAsync(string token, CancellationToken ct = default);

    Task AddLinkAsync(MeetingMinutesShareLink link, CancellationToken ct = default);

    /// <summary>Everybody invited to this room's minutes, in the order they were added.</summary>
    Task<List<MeetingMinutesShareGrant>> GetGrantsAsync(Guid roomId, CancellationToken ct = default);

    /// <summary>
    /// Whether this email is on the list. Matched lower-cased: a person typing their own address
    /// is not consistent about case.
    /// </summary>
    Task<bool> HasGrantAsync(Guid roomId, string email, CancellationToken ct = default);

    Task AddGrantAsync(MeetingMinutesShareGrant grant, CancellationToken ct = default);

    /// <summary>Removes one person from the list. Silent when they were not on it.</summary>
    Task RemoveGrantAsync(Guid roomId, string email, CancellationToken ct = default);
}

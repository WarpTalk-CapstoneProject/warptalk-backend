using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Interfaces;

public interface IVoiceProfileService
{
    Task<Result<IReadOnlyList<VoiceProfileDto>>> GetProfilesAsync(Guid userId, CancellationToken ct = default);
    Task<Result<VoiceProfileDto>> CreateProfileAsync(Guid userId, CreateVoiceProfileRequest request, CancellationToken ct = default);
    Task<Result> DeleteProfileAsync(Guid userId, Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Voices selectable for <paramref name="language"/>. An empty list is a valid answer,
    /// not a failure — see IVoiceCatalogDirectory.
    /// </summary>
    Task<Result<IReadOnlyList<VoiceCatalogItemDto>>> GetCatalogAsync(string language, CancellationToken ct = default);

    /// <summary>
    /// Set (or clear, with a null/empty VoiceId) the library voice this user hears for one
    /// language. At most one preference exists per user per language; the returned profile is
    /// null when the preference was cleared.
    /// </summary>
    Task<Result<VoiceProfileDto?>> SetPreferredVoiceAsync(Guid userId, SetPreferredVoiceRequest request, CancellationToken ct = default);

    /// <summary>
    /// WT-396 — the voice this user is DUBBED IN. The other direction from SetPreferredVoiceAsync
    /// above, which is the voice they HEAR other people in; the two were the same table and a
    /// chosen voice went to the wrong one.
    ///
    /// Accepts either a voice from the public catalogue or the provider id behind one of this
    /// user's own profiles, and nothing else: an unvalidated id reaches Cartesia as an unknown
    /// voice and the dub silently falls back, which is exactly the failure being fixed.
    ///
    /// A null or empty VoiceId clears the choice and returns to cloning live from the meeting.
    /// </summary>
    Task<Result<string?>> SetDubVoiceAsync(Guid userId, SetDubVoiceRequest request, CancellationToken ct = default);

    /// <summary>The user's chosen dub voice, or null when they have not chosen one.</summary>
    Task<Result<string?>> GetDubVoiceAsync(Guid userId, CancellationToken ct = default);
}

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
}

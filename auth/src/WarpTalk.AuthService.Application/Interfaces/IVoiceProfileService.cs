using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Interfaces;

public interface IVoiceProfileService
{
    Task<Result<IReadOnlyList<VoiceProfileDto>>> GetProfilesAsync(Guid userId, Guid? workspaceId = null, CancellationToken ct = default);
    Task<Result<VoiceProfileDto>> GetProfileAsync(Guid userId, Guid profileId, CancellationToken ct = default);
    Task<Result<VoiceProfileDto>> CreateProfileAsync(Guid userId, CreateVoiceProfileRequest request, CancellationToken ct = default);
    Task<Result<VoiceProfileDto>> UpdateProfileAsync(Guid userId, Guid profileId, UpdateVoiceProfileRequest request, CancellationToken ct = default);
    Task<Result> DeleteProfileAsync(Guid userId, Guid profileId, CancellationToken ct = default);
    Task<Result<VoiceProfileDto>> AddSampleAsync(Guid userId, Guid profileId, AddVoiceSampleRequest request, CancellationToken ct = default);
    Task<Result<VoiceProfileDto>> GrantConsentAsync(Guid userId, Guid profileId, GrantVoiceConsentRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<Result<VoiceProfileDto>> RevokeConsentAsync(Guid userId, Guid profileId, RevokeVoiceConsentRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
}

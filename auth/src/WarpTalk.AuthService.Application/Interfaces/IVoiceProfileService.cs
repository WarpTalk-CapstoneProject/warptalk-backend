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
}

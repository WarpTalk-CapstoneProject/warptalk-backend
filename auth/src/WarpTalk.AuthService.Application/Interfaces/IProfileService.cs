using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Interfaces;

public interface IProfileService
{
    Task<Result<UserDto>> GetProfileAsync(Guid userId, CancellationToken ct = default);
    Task<Result<UserDto>> UpdateProfileAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct = default);

    /// <summary>
    /// Replace this user's avatar with the uploaded image, and return the profile as it now is.
    ///
    /// One user has one avatar, so this overwrites rather than accumulating — "change my avatar"
    /// is the only thing anybody asks of it.
    /// </summary>
    Task<Result<UserDto>> UpdateAvatarAsync(
        Guid userId, Stream content, string? contentType, long length, CancellationToken ct = default);

    /// <summary>
    /// The stored avatar bytes, or a failure when this user has never uploaded one.
    ///
    /// Anonymous on the read side: an &lt;img&gt; tag carries no Authorization header, and a
    /// picture of somebody's face keyed by their own user id is not a secret worth breaking
    /// every avatar in the product to protect.
    /// </summary>
    Task<Result<ProfileAvatarContent>> GetAvatarAsync(
        Guid userId, string extension, CancellationToken ct = default);
    Task<Result> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default);
}

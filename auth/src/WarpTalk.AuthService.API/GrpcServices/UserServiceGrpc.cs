using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;

namespace WarpTalk.AuthService.API.GrpcServices;

public class UserServiceGrpc : UserService.UserServiceBase
{
    private readonly IUserDirectoryService _userDirectory;
    private readonly IVoiceConsentService _voiceConsent;
    private readonly IVoiceProfileService _voiceProfiles;

    public UserServiceGrpc(
        IUserDirectoryService userDirectory,
        IVoiceConsentService voiceConsent,
        IVoiceProfileService voiceProfiles)
    {
        _userDirectory = userDirectory;
        _voiceConsent = voiceConsent;
        _voiceProfiles = voiceProfiles;
    }

    /// <summary>
    /// The gate in front of biometric processing, answered for another service.
    ///
    /// An unparseable id returns false rather than throwing. Every other method here throws on a
    /// bad id because the caller asked about a specific user and deserves to know the request was
    /// malformed — but this one is a permission check, and a permission check that throws is a
    /// permission check the caller might catch and treat as "carry on".
    /// </summary>
    public override async Task<HasVoiceCloneConsentResponse> HasVoiceCloneConsent(
        GetUserRequest request,
        ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var parsedId))
            return new HasVoiceCloneConsentResponse { Granted = false };

        var granted = await _voiceConsent.HasActiveConsentAsync(parsedId, CancellationTokenOf(context));
        return new HasVoiceCloneConsentResponse { Granted = granted };
    }

    /// <summary>
    /// WT-396. Empty is a valid answer meaning "they have not chosen one" — the caller falls back
    /// to cloning the speaker live, which is what happens for everyone today. An unparseable id
    /// answers the same way rather than throwing: a voice preference must never be able to stop a
    /// meeting's audio routes from being built.
    /// </summary>
    public override async Task<GetPreferredVoiceResponse> GetPreferredVoice(
        GetUserRequest request,
        ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var parsedId))
            return new GetPreferredVoiceResponse();

        var ct = CancellationTokenOf(context);
        var response = new GetPreferredVoiceResponse();

        var chosen = await _voiceProfiles.GetDubVoiceAsync(parsedId, ct);
        if (chosen.IsSuccess && !string.IsNullOrWhiteSpace(chosen.Value))
        {
            response.VoiceId = chosen.Value;
            response.Provider = "cartesia";
        }

        // WT-B. Answered even when a deliberate pick exists above, and NOT folded into it.
        //
        // The two are different instructions to the worker: a pick means stop capturing, a
        // carried clone means keep improving. Sending the carried one only when there is no pick
        // would be a smaller change and a worse one — the worker decides the precedence, and it
        // cannot decide what it was never told.
        var carried = await _voiceProfiles.GetAutoCloneVoiceAsync(parsedId, ct);
        if (carried.IsSuccess && !string.IsNullOrWhiteSpace(carried.Value.VoiceId))
        {
            response.AutoCloneVoiceId = carried.Value.VoiceId;
            response.Provider = "cartesia";

            // Formatted with InvariantCulture, matching how the AI side wrote it. Left to the
            // ambient culture, a comma-decimal server would send "0,812" and the far side would
            // read it as unparseable — dropping the bar to null and letting any clip at all
            // replace a good voice.
            if (carried.Value.Score is { } score)
            {
                response.AutoCloneScore = score.ToString(
                    "0.###", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return response;
    }

    public override async Task<GetUserResponse> GetUserById(GetUserRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var parsedId))
            throw GrpcErrors.InvalidId("User");

        var result = await _userDirectory.GetUserByIdAsync(parsedId, CancellationTokenOf(context));
        if (!result.IsSuccess)
            throw GrpcErrors.NotFound("User", request.Id);

        return ToUserResponse(result.Value!);
    }

    public override async Task<GetUserResponse> GetUserByEmail(GetUserByEmailRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Email is required."));

        var result = await _userDirectory.GetUserByEmailAsync(request.Email, CancellationTokenOf(context));
        if (!result.IsSuccess)
            throw GrpcErrors.NotFound("User", request.Email);

        return ToUserResponse(result.Value!);
    }

    public override async Task<GetUserSettingsResponse> GetUserSettings(
        GetUserRequest request,
        ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var parsedId))
            throw GrpcErrors.InvalidId("User");

        var result = await _userDirectory.GetLanguageDefaultsAsync(parsedId, CancellationTokenOf(context));
        if (!result.IsSuccess || result.Value is null)
            return new GetUserSettingsResponse { Found = false };

        return new GetUserSettingsResponse
        {
            Found = true,
            DefaultSpeakLanguage = result.Value.DefaultSpeakLanguage,
            DefaultListenLanguage = result.Value.DefaultListenLanguage,
            VoiceCloneEnabled = result.Value.VoiceCloneEnabled
        };
    }

    public override async Task<GetRoleResponse> GetRoleByName(GetRoleByNameRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Role name is required."));

        var result = await _userDirectory.GetRoleByNameAsync(request.Name, CancellationTokenOf(context));
        if (!result.IsSuccess)
            throw GrpcErrors.NotFound("Role", request.Name);

        return ToRoleResponse(result.Value!);
    }

    public override async Task<GetRoleResponse> GetRoleById(GetRoleByIdRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var parsedId))
            throw GrpcErrors.InvalidId("Role");

        var result = await _userDirectory.GetRoleByIdAsync(parsedId, CancellationTokenOf(context));
        if (!result.IsSuccess)
            throw GrpcErrors.NotFound("Role", request.Id);

        return ToRoleResponse(result.Value!);
    }

    // GetUserSettings already tolerated a null context because the unit test passes one.
    // Applied uniformly so every method behaves the same way under test.
    private static CancellationToken CancellationTokenOf(ServerCallContext context) =>
        context?.CancellationToken ?? CancellationToken.None;

    private static GetUserResponse ToUserResponse(UserIdentityDto user) => new()
    {
        Id = user.Id.ToString(),
        Email = user.Email,
        FullName = user.FullName,
        AvatarUrl = user.AvatarUrl ?? "",
        PreferredLanguage = user.PreferredLanguage ?? "en"
    };

    private static GetRoleResponse ToRoleResponse(RoleDto role) => new()
    {
        Id = role.Id.ToString(),
        Name = role.Name,
        Description = role.Description ?? ""
    };
}

using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Tests;

public class VoiceProfileServiceTests
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserRepository _userRepository;
    private readonly IVoiceProfileRepository _voiceProfileRepository;
    private readonly IVoiceConsentRepository _voiceConsentRepository;
    private readonly IVoiceSampleRepository _voiceSampleRepository;
    private readonly VoiceProfileService _service;

    public VoiceProfileServiceTests()
    {
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _userRepository = Substitute.For<IUserRepository>();
        _voiceProfileRepository = Substitute.For<IVoiceProfileRepository>();
        _voiceConsentRepository = Substitute.For<IVoiceConsentRepository>();
        _voiceSampleRepository = Substitute.For<IVoiceSampleRepository>();

        _unitOfWork.UserRepository.Returns(_userRepository);
        _unitOfWork.VoiceProfileRepository.Returns(_voiceProfileRepository);
        _unitOfWork.VoiceConsentRepository.Returns(_voiceConsentRepository);
        _unitOfWork.VoiceSampleRepository.Returns(_voiceSampleRepository);

        _service = new VoiceProfileService(_unitOfWork, Substitute.For<ILogger<VoiceProfileService>>());
    }

    [Fact]
    public async Task CreateProfileAsync_ShouldCreatePendingConsentProfile_WhenUserIsActive()
    {
        var userId = Guid.NewGuid();
        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(CreateActiveUser(userId));

        var request = new CreateVoiceProfileRequest("Host neutral", null, "xtts-v2");

        var result = await _service.CreateProfileAsync(userId, request);

        Assert.True(result.IsSuccess);
        Assert.Equal("Host neutral", result.Value!.DisplayName);
        Assert.Equal(VoiceProfileConstants.StatusPendingConsent, result.Value.Status);
        await _voiceProfileRepository.Received(1).AddAsync(Arg.Is<VoiceProfile>(p => p.UserId == userId), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateProfileAsync_ShouldFail_WhenMarkingReadyWithoutConsent()
    {
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var profile = CreateProfile(profileId, userId);

        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(CreateActiveUser(userId));
        _voiceProfileRepository.GetByIdForUserAsync(profileId, userId, Arg.Any<CancellationToken>()).Returns(profile);
        _voiceConsentRepository.HasGrantedConsentAsync(profileId, VoiceProfileConstants.ConsentTypeVoiceClone, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _service.UpdateProfileAsync(userId, profileId, new UpdateVoiceProfileRequest(Status: VoiceProfileConstants.StatusReady));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        _voiceProfileRepository.DidNotReceive().Update(Arg.Any<VoiceProfile>());
    }

    [Fact]
    public async Task AddSampleAsync_ShouldPersistSampleReference_WhenProfileBelongsToUser()
    {
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var profile = CreateProfile(profileId, userId);

        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(CreateActiveUser(userId));
        _voiceProfileRepository.GetByIdForUserAsync(profileId, userId, Arg.Any<CancellationToken>()).Returns(profile);

        var request = new AddVoiceSampleRequest("uploaded", "https://storage.example.com/sample.wav", 30, "vi-VN");

        var result = await _service.AddSampleAsync(userId, profileId, request);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Samples);
        await _voiceSampleRepository.Received(1).AddAsync(Arg.Is<VoiceSample>(s => s.FileUrl == request.FileUrl), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GrantConsentAsync_ShouldAddGrantedConsentAndMoveProfileToDraft()
    {
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var profile = CreateProfile(profileId, userId);

        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(CreateActiveUser(userId));
        _voiceProfileRepository.GetByIdForUserAsync(profileId, userId, Arg.Any<CancellationToken>()).Returns(profile);

        var request = new GrantVoiceConsentRequest("voice_clone", "voice-consent-v1");

        var result = await _service.GrantConsentAsync(userId, profileId, request, "127.0.0.1", "test-agent");

        Assert.True(result.IsSuccess);
        Assert.Equal(VoiceProfileConstants.StatusDraft, result.Value!.Status);
        Assert.Single(result.Value.Consents);
        await _voiceConsentRepository.Received(1).AddAsync(Arg.Is<VoiceConsent>(c => c.UserId == userId), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetProfileAsync_ShouldReturnNotFound_WhenProfileDoesNotBelongToUser()
    {
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        _userRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(CreateActiveUser(userId));
        _voiceProfileRepository.GetByIdForUserAsync(profileId, userId, Arg.Any<CancellationToken>()).Returns((VoiceProfile?)null);

        var result = await _service.GetProfileAsync(userId, profileId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    private static User CreateActiveUser(Guid userId)
    {
        return new User
        {
            Id = userId,
            Email = "user@warptalk.vn",
            FullName = "Test User",
            IsActive = true,
            EmailVerified = true
        };
    }

    private static VoiceProfile CreateProfile(Guid profileId, Guid userId)
    {
        return new VoiceProfile
        {
            Id = profileId,
            UserId = userId,
            DisplayName = "Host neutral",
            Provider = "xtts-v2",
            Status = VoiceProfileConstants.StatusPendingConsent,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }
}

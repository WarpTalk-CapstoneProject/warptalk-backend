using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests;

/// <summary>
/// The preferred-voice half of VoiceProfileService: picking a library voice for a language
/// and having it survive as a voice_profiles row (provider + embedding_ref), which the client
/// later round-trips into TranslationRoomHub.SetVoicePreference.
/// </summary>
public class VoiceProfileServiceTests
{
    private const string Vi = "vi";
    private const string LinhVoiceId = "935a9060-373c-49e4-b078-f4ea6326987a";
    private const string MinhVoiceId = "0e58d60a-2f1a-4252-81bd-3db6af45fb41";

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IVoiceProfileRepository _profiles = Substitute.For<IVoiceProfileRepository>();
    private readonly IVoiceSampleRepository _samples = Substitute.For<IVoiceSampleRepository>();
    private readonly IVoiceConsentRepository _consents = Substitute.For<IVoiceConsentRepository>();
    private readonly IVoiceSampleStorage _storage = Substitute.For<IVoiceSampleStorage>();
    private readonly IVoiceCatalogDirectory _catalog = Substitute.For<IVoiceCatalogDirectory>();
    private readonly VoiceProfileService _service;

    public VoiceProfileServiceTests()
    {
        _unitOfWork.VoiceProfileRepository.Returns(_profiles);
        _unitOfWork.VoiceSampleRepository.Returns(_samples);
        _unitOfWork.VoiceConsentRepository.Returns(_consents);
        _profiles.GetByUserIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<VoiceProfile>());
        _storage.SaveAsync(Arg.Any<string>(), Arg.Any<System.IO.Stream>(), Arg.Any<CancellationToken>())
            .Returns(c => (string)c[0]);
        StubCatalog(
            new VoiceCatalogItemDto(LinhVoiceId, "Linh - Soft Presence", "feminine"),
            new VoiceCatalogItemDto(MinhVoiceId, "Minh - Conversational Partner", "masculine"));

        _service = new VoiceProfileService(
            _unitOfWork, _storage, _catalog, Substitute.For<ILogger<VoiceProfileService>>());
    }

    private void StubCatalog(params VoiceCatalogItemDto[] voices) =>
        _catalog.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<VoiceCatalogItemDto>)voices);

    private static VoiceProfile ExistingPick(Guid userId, string language, string voiceId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Language = language,
        Provider = "cartesia",
        EmbeddingRef = voiceId,
        Status = "active",
        IsActive = true,
        VoiceSamples = new List<VoiceSample>(),
    };

    private static FormFile ValidVoiceSample() => new(
        new System.IO.MemoryStream(new byte[] { 1, 2, 3 }),
        0,
        3,
        "sample",
        "voice.wav")
    {
        Headers = new HeaderDictionary(),
        ContentType = "audio/wav",
    };

    [Fact]
    public async Task GetCatalogAsync_ShouldReturnTheDirectoryList()
    {
        var result = await _service.GetCatalogAsync(Vi);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new[] { LinhVoiceId, MinhVoiceId },
            result.Value!.Select(v => v.Id).ToArray());
    }

    [Fact]
    public async Task GetCatalogAsync_ShouldSucceedWithAnEmptyList_WhenTheCacheIsCold()
    {
        // A cold catalog is a normal state (the AI worker fills it on its next synthesis),
        // so it must not surface as an error the page has to special-case.
        StubCatalog();

        var result = await _service.GetCatalogAsync(Vi);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task GetCatalogAsync_ShouldFail_WhenLanguageIsMissing()
    {
        var result = await _service.GetCatalogAsync("  ");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task SetPreferredVoiceAsync_ShouldCreateAProfileCarryingProviderAndVoiceId()
    {
        var userId = Guid.NewGuid();
        VoiceProfile? added = null;
        _profiles.When(r => r.Add(Arg.Any<VoiceProfile>())).Do(c => added = c.Arg<VoiceProfile>());

        var result = await _service.SetPreferredVoiceAsync(userId, new SetPreferredVoiceRequest(Vi, LinhVoiceId));

        Assert.True(result.IsSuccess);
        Assert.NotNull(added);
        Assert.Equal(userId, added!.UserId);
        Assert.Equal("cartesia", added.Provider);
        Assert.Equal(LinhVoiceId, added.EmbeddingRef);
        Assert.Equal(Vi, added.Language);
        Assert.True(added.IsActive);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());

        // The client needs the id back out to hand it to SetVoicePreference.
        Assert.Equal(LinhVoiceId, result.Value!.ProviderVoiceId);
        Assert.Equal("cartesia", result.Value.Provider);
    }

    [Fact]
    public async Task SetPreferredVoiceAsync_ShouldUpdateInPlace_RatherThanStackingProfilesPerLanguage()
    {
        var userId = Guid.NewGuid();
        var existing = ExistingPick(userId, Vi, LinhVoiceId);
        _profiles.GetByUserIdAsync(userId, Arg.Any<CancellationToken>()).Returns(new[] { existing });

        var result = await _service.SetPreferredVoiceAsync(userId, new SetPreferredVoiceRequest(Vi, MinhVoiceId));

        Assert.True(result.IsSuccess);
        Assert.Equal(MinhVoiceId, existing.EmbeddingRef);
        _profiles.Received(1).Update(existing);
        _profiles.DidNotReceive().Add(Arg.Any<VoiceProfile>());
    }

    [Fact]
    public async Task SetPreferredVoiceAsync_ShouldNotTouchAnotherLanguagesPick()
    {
        // Preferences are per language — choosing a Vietnamese voice must leave the English
        // one alone, because the catalog itself is per language.
        var userId = Guid.NewGuid();
        var english = ExistingPick(userId, "en", "some-english-voice");
        _profiles.GetByUserIdAsync(userId, Arg.Any<CancellationToken>()).Returns(new[] { english });

        var result = await _service.SetPreferredVoiceAsync(userId, new SetPreferredVoiceRequest(Vi, LinhVoiceId));

        Assert.True(result.IsSuccess);
        Assert.Equal("some-english-voice", english.EmbeddingRef);
        _profiles.Received(1).Add(Arg.Any<VoiceProfile>());
    }

    [Fact]
    public async Task SetPreferredVoiceAsync_ShouldRejectAVoiceNotOfferedForThatLanguage()
    {
        // Otherwise the bad id is stored, round-tripped into SetVoicePreference, and fails
        // silently inside the TTS worker where nobody is looking.
        var result = await _service.SetPreferredVoiceAsync(
            Guid.NewGuid(), new SetPreferredVoiceRequest(Vi, "not-in-the-catalog"));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        _profiles.DidNotReceive().Add(Arg.Any<VoiceProfile>());
    }

    [Fact]
    public async Task SetPreferredVoiceAsync_ShouldRefuse_WhenTheCatalogIsNotWarmedYet()
    {
        StubCatalog();

        var result = await _service.SetPreferredVoiceAsync(
            Guid.NewGuid(), new SetPreferredVoiceRequest(Vi, LinhVoiceId));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidState, result.ErrorCode);
    }

    [Fact]
    public async Task SetPreferredVoiceAsync_ShouldClearThePreference_WhenVoiceIdIsEmpty()
    {
        var userId = Guid.NewGuid();
        var existing = ExistingPick(userId, Vi, LinhVoiceId);
        _profiles.GetByUserIdAsync(userId, Arg.Any<CancellationToken>()).Returns(new[] { existing });

        var result = await _service.SetPreferredVoiceAsync(userId, new SetPreferredVoiceRequest(Vi, ""));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.NotNull(existing.DeletedAt);
        Assert.False(existing.IsActive);
        _profiles.Received(1).Update(existing);
    }

    [Fact]
    public async Task SetPreferredVoiceAsync_ShouldBeIdempotent_WhenClearingWithNothingStored()
    {
        var result = await _service.SetPreferredVoiceAsync(Guid.NewGuid(), new SetPreferredVoiceRequest(Vi, null));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetPreferredVoiceAsync_ShouldFail_WhenLanguageIsMissing()
    {
        var result = await _service.SetPreferredVoiceAsync(Guid.NewGuid(), new SetPreferredVoiceRequest("", LinhVoiceId));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task CreateProfileAsync_ShouldRejectProfileWithoutValidatedVoiceSample()
    {
        var result = await _service.CreateProfileAsync(
            Guid.NewGuid(),
            new CreateVoiceProfileRequest
            {
                DisplayName = "My voice",
                Language = "vi-VN",
                Sample = null,
            });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Contains("sample", result.Error!, StringComparison.OrdinalIgnoreCase);
        _profiles.DidNotReceive().Add(Arg.Any<VoiceProfile>());
    }

    [Fact]
    public async Task CreateProfileAsync_ShouldRejectUploadedSample_WhenConsentIsIncomplete()
    {
        var userId = Guid.NewGuid();
        var sample = ValidVoiceSample();

        var result = await _service.CreateProfileAsync(
            userId,
            new CreateVoiceProfileRequest
            {
                DisplayName = "My voice",
                Language = "vi-VN",
                Sample = sample,
                OwnVoiceConfirmed = true,
                AiUseConfirmed = true,
                SyntheticVoiceAcknowledged = true,
                NoImpersonationConfirmed = false,
                RetentionAcknowledged = true,
            });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Contains("consent", result.Error!, StringComparison.OrdinalIgnoreCase);
        _profiles.DidNotReceive().Add(Arg.Any<VoiceProfile>());
        await _samples.DidNotReceive().AddAsync(Arg.Any<VoiceSample>(), Arg.Any<CancellationToken>());
        await _storage.DidNotReceive().SaveAsync(Arg.Any<string>(), Arg.Any<System.IO.Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateProfileAsync_ShouldStoreConsentContract_WhenUploadedSampleConsentIsComplete()
    {
        var userId = Guid.NewGuid();
        VoiceProfile? addedProfile = null;
        VoiceConsent? addedConsent = null;
        _profiles.When(r => r.Add(Arg.Any<VoiceProfile>())).Do(c => addedProfile = c.Arg<VoiceProfile>());
        _consents
            .When(r => r.AddAsync(Arg.Any<VoiceConsent>(), Arg.Any<CancellationToken>()))
            .Do(c => addedConsent = c.Arg<VoiceConsent>());

        var result = await _service.CreateProfileAsync(
            userId,
            new CreateVoiceProfileRequest
            {
                DisplayName = "My voice",
                Language = "vi-VN",
                Sample = ValidVoiceSample(),
                OwnVoiceConfirmed = true,
                AiUseConfirmed = true,
                SyntheticVoiceAcknowledged = true,
                NoImpersonationConfirmed = true,
                RetentionAcknowledged = true,
            },
            ipAddress: "203.0.113.10",
            userAgent: "WarpTalkTest/1.0");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        var dto = result.Value!;
        Assert.NotNull(addedProfile);
        Assert.NotNull(addedConsent);
        Assert.Equal(userId, addedConsent!.UserId);
        Assert.Equal(addedProfile!.Id, addedConsent.VoiceProfileId);
        Assert.Equal("voice_profile_upload", addedConsent.ConsentType);
        Assert.Equal("GRANTED", addedConsent.ConsentStatus);
        Assert.Equal("voice-profile-upload-v1", addedConsent.ConsentTextVersion);
        Assert.NotNull(addedConsent.GrantedAt);
        Assert.Equal("203.0.113.10", addedConsent.IpAddress);
        Assert.Equal("WarpTalkTest/1.0", addedConsent.UserAgent);
        Assert.NotNull(addedConsent.ContractSnapshot);
        Assert.NotNull(addedConsent.ContractHash);
        Assert.Equal(64, addedConsent.ContractHash!.Length);
        Assert.True(addedConsent.OwnVoiceConfirmed);
        Assert.True(addedConsent.AiUseConfirmed);
        Assert.True(addedConsent.SyntheticVoiceAcknowledged);
        Assert.True(addedConsent.NoImpersonationConfirmed);
        Assert.True(addedConsent.RetentionAcknowledged);
        Assert.Equal("granted", dto.ConsentStatus);
        Assert.Equal("voice-profile-upload-v1", dto.ConsentTextVersion);
        Assert.NotNull(dto.ConsentGrantedAt);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private VoiceProfile ProfileWithSamples(Guid userId, params string[] fileUrls)
    {
        var profile = new VoiceProfile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DisplayName = "My voice",
            Language = Vi,
            Status = "active",
            IsActive = true,
            VoiceSamples = fileUrls.Select(url => new VoiceSample
            {
                Id = Guid.NewGuid(),
                SampleType = "reference",
                FileUrl = url,
                Language = Vi,
                ContainsRawAudio = true,
            }).ToList(),
        };
        foreach (var sample in profile.VoiceSamples)
        {
            sample.VoiceProfileId = profile.Id;
        }

        _profiles.GetByIdForUserAsync(profile.Id, userId, Arg.Any<CancellationToken>()).Returns(profile);
        return profile;
    }

    [Fact]
    public async Task DeleteProfileAsync_ShouldSoftDeleteTheSampleRows_NotJustTheProfile()
    {
        // WT-276: the storage objects were deleted while every voice_samples row kept
        // deleted_at = NULL, so the row still claimed a sample the bucket no longer had.
        var userId = Guid.NewGuid();
        var profile = ProfileWithSamples(userId, "sample-a.wav", "sample-b.wav");

        var result = await _service.DeleteProfileAsync(userId, profile.Id);

        Assert.True(result.IsSuccess);
        Assert.NotNull(profile.DeletedAt);
        Assert.All(profile.VoiceSamples, sample =>
        {
            Assert.NotNull(sample.DeletedAt);
            Assert.Equal(userId, sample.DeletedBy);
        });

        // One save, so the sample rows land in the same unit of work as the profile.
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _storage.Received(1).DeleteAsync("sample-a.wav", Arg.Any<CancellationToken>());
        await _storage.Received(1).DeleteAsync("sample-b.wav", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteProfileAsync_ShouldCommitTheRows_BeforeRemovingTheObjects()
    {
        // Pins the ordering: an orphaned object is the failure we accept, an orphaned row
        // is the one we do not. Deleting the objects first would reopen WT-276 whenever the
        // save failed.
        var userId = Guid.NewGuid();
        var profile = ProfileWithSamples(userId, "sample-a.wav");

        await _service.DeleteProfileAsync(userId, profile.Id);

        Received.InOrder(() =>
        {
            _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>());
            _storage.DeleteAsync("sample-a.wav", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task DeleteProfileAsync_ShouldSucceedAndKeepGoing_WhenOneObjectDeleteThrows()
    {
        // The rows are already committed as deleted, so a retry would only return NotFound.
        // A failed object delete leaks bytes; it must not report the profile as still there
        // or abandon the remaining samples.
        var userId = Guid.NewGuid();
        var profile = ProfileWithSamples(userId, "sample-a.wav", "sample-b.wav");
        _storage.DeleteAsync("sample-a.wav", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("bucket unreachable")));

        var result = await _service.DeleteProfileAsync(userId, profile.Id);

        Assert.True(result.IsSuccess);
        Assert.All(profile.VoiceSamples, sample => Assert.NotNull(sample.DeletedAt));
        await _storage.Received(1).DeleteAsync("sample-b.wav", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteProfileAsync_ShouldNotSave_WhenTheProfileIsNotTheCallers()
    {
        var result = await _service.DeleteProfileAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _storage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}

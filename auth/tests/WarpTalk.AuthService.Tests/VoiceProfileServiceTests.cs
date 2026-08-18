using System;
using System.Collections.Generic;
using System.IO;
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
    private readonly IVoiceCloneRequestQueue _cloneQueue;
    private readonly IVoicePreviewQueue _previewQueue;
    private readonly IVoiceCatalogDirectory _catalog = Substitute.For<IVoiceCatalogDirectory>();
    private readonly VoiceProfileService _service;

    public VoiceProfileServiceTests()
    {
        _unitOfWork.VoiceProfileRepository.Returns(_profiles);
        _unitOfWork.VoiceSampleRepository.Returns(_samples);
        _unitOfWork.VoiceConsentRepository.Returns(_consents);
        _profiles.GetByUserIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<VoiceProfile>());
        StubCatalog(
            new VoiceCatalogItemDto(LinhVoiceId, "Linh - Soft Presence", "feminine"),
            new VoiceCatalogItemDto(MinhVoiceId, "Minh - Conversational Partner", "masculine"));

        // Silent by default: the clone hand-off is pinned in UploadCloneHandoffTests, and a
        // queue that answered here would change what every assertion below is testing.
        _cloneQueue = Substitute.For<IVoiceCloneRequestQueue>();
        // Silent for the same reason, and it matters more here: an unstubbed WaitAsync answers
        // null, so a preview test that forgot to arrange one fails on the timeout branch rather
        // than passing on a substitute's default.
        _previewQueue = Substitute.For<IVoicePreviewQueue>();
        _service = new VoiceProfileService(
            _unitOfWork, _storage, _catalog, _cloneQueue, _previewQueue,
            Substitute.For<ILogger<VoiceProfileService>>());
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
            });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        var dto = result.Value!;
        Assert.NotNull(addedProfile);
        Assert.NotNull(addedConsent);
        Assert.Equal(userId, addedConsent!.UserId);
        Assert.Equal(addedProfile!.Id, addedConsent.VoiceProfileId);
        Assert.Equal("VOICE_PROFILE_UPLOAD", addedConsent.ConsentType);
        Assert.Equal("GRANTED", addedConsent.ConsentStatus);
        Assert.StartsWith("voice-v1:", addedConsent.ConsentTextVersion);
        Assert.NotNull(addedConsent.GrantedAt);
        Assert.Equal("granted", dto.ConsentStatus);
        Assert.StartsWith("voice-v1:", dto.ConsentTextVersion);
        Assert.NotNull(dto.ConsentGrantedAt);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateProfileAsync_ShouldRejectSample_WhenMagicBytesAreInvalid()
    {
        var invalidFile = Substitute.For<IFormFile>();
        invalidFile.ContentType.Returns("audio/wav");
        invalidFile.Length.Returns(100);
        invalidFile.FileName.Returns("malicious.wav");
        // Script or executable payload starting with 'MZ' (Windows EXE)
        invalidFile.OpenReadStream().Returns(_ => new MemoryStream(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }));

        var result = await _service.CreateProfileAsync(
            Guid.NewGuid(),
            new CreateVoiceProfileRequest
            {
                DisplayName = "My voice",
                Language = Vi,
                Sample = invalidFile,
                OwnVoiceConfirmed = true,
                AiUseConfirmed = true,
                SyntheticVoiceAcknowledged = true,
                NoImpersonationConfirmed = true,
                RetentionAcknowledged = true,
            });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Contains("signature", result.Error!, StringComparison.OrdinalIgnoreCase);
        _profiles.DidNotReceive().Add(Arg.Any<VoiceProfile>());
    }

    private static FormFile ValidVoiceSample() => new(
        new MemoryStream(new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00 }),
        0,
        8,
        "sample",
        "voice.wav")
    {
        Headers = new HeaderDictionary(),
        ContentType = "audio/wav",
    };

    // ── The sample's media type (WT-372) ────────────────────────────────────────────────────
    //
    // Nothing here touched Sample.ContentType before, which is exactly why the recorded-sample
    // path could be broken for every browser at once and stay broken: the only samples the suite
    // had ever seen were the null one above.

    /// <summary>
    /// The reported WT-372 failure, verbatim from the browser.
    ///
    /// `MediaRecorder` is constructed with "audio/webm;codecs=opus" and reports that string back
    /// as its `mimeType`, so the File the page builds — and therefore the part's Content-Type —
    /// carries the codecs parameter. Against a bare-type allowlist that misses by the suffix, and
    /// the owner sees "Failed to create voice profile" with no way to tell why.
    /// </summary>
    [Theory]
    [InlineData("audio/webm;codecs=opus")]        // Chrome / Edge — the reported case
    [InlineData("audio/ogg;codecs=opus")]         // Firefox
    [InlineData("audio/mp4;codecs=mp4a.40.2")]    // Safari
    [InlineData("audio/webm; codecs=opus")]       // a space after the separator is equally legal
    [InlineData("audio/wav")]                     // the upload path, which always worked
    public async Task CreateProfileAsync_ShouldAcceptSample_WhenMediaTypeCarriesParameters(string contentType)
    {
        var result = await _service.CreateProfileAsync(
            Guid.NewGuid(),
            new CreateVoiceProfileRequest
            {
                DisplayName = "My voice",
                Language = Vi,
                Sample = Sample(contentType),
                OwnVoiceConfirmed = true,
                AiUseConfirmed = true,
                SyntheticVoiceAcknowledged = true,
                NoImpersonationConfirmed = true,
                RetentionAcknowledged = true,
            });

        Assert.True(result.IsSuccess, $"'{contentType}' was rejected: {result.Error}");
        _profiles.Received(1).Add(Arg.Any<VoiceProfile>());
    }

    /// <summary>
    /// Stripping the parameters must not turn the allowlist off. A parameter on a type that was
    /// never allowed is still not allowed, and neither is a type that merely starts with one.
    /// </summary>
    [Theory]
    [InlineData("video/mp4;codecs=avc1")]
    [InlineData("application/octet-stream")]
    [InlineData("audio/webmsomethingelse")]
    [InlineData("")]
    public async Task CreateProfileAsync_ShouldStillRejectSample_WhenMediaTypeIsNotAllowed(string contentType)
    {
        var result = await _service.CreateProfileAsync(
            Guid.NewGuid(),
            new CreateVoiceProfileRequest
            {
                DisplayName = "My voice",
                Language = Vi,
                Sample = Sample(contentType),
                OwnVoiceConfirmed = true,
                AiUseConfirmed = true,
                SyntheticVoiceAcknowledged = true,
                NoImpersonationConfirmed = true,
                RetentionAcknowledged = true,
            });

        Assert.False(result.IsSuccess, $"'{contentType}' was accepted");
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        _profiles.DidNotReceive().Add(Arg.Any<VoiceProfile>());
    }

    /// <summary>One non-empty audio part, described by <paramref name="contentType"/>.</summary>
    private static IFormFile Sample(string contentType, long length = 64 * 1024)
    {
        var file = Substitute.For<IFormFile>();
        file.ContentType.Returns(contentType);
        file.Length.Returns(length);
        file.FileName.Returns("voice-sample.webm");
        // RIFF header for valid audio magic bytes
        file.OpenReadStream().Returns(_ => new MemoryStream(new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00 }));
        return file;
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

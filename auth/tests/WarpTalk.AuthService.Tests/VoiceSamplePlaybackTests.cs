using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests;

/// <summary>
/// Playing back the recording somebody uploaded, rather than the clone made from it.
///
/// WHY THE FEATURE EXISTS
///     "Is this a good clone of me?" is a question about the DISTANCE between two sounds, and the
///     product only ever played one of them. Preview is the clone speaking; this is the original.
///
/// WHY THESE TESTS ARE MOSTLY ABOUT ACCESS
///     The bytes here are a recording of a person's voice, kept because they agreed to have it
///     cloned — not because they agreed to publish it. Every way of reaching somebody else's
///     recording is worth a test of its own.
/// </summary>
public class VoiceSamplePlaybackTests
{
    private static readonly byte[] Recording = Encoding.ASCII.GetBytes("RIFFthe-original-recording");

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _profileId = Guid.NewGuid();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IVoiceProfileRepository _profiles = Substitute.For<IVoiceProfileRepository>();
    private readonly IVoiceSampleRepository _samples = Substitute.For<IVoiceSampleRepository>();
    private readonly IVoiceSampleStorage _storage = Substitute.For<IVoiceSampleStorage>();
    private readonly VoiceProfileService _service;

    public VoiceSamplePlaybackTests()
    {
        _unitOfWork.VoiceProfileRepository.Returns(_profiles);
        _unitOfWork.VoiceSampleRepository.Returns(_samples);
        _storage.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(Recording)));

        _service = new VoiceProfileService(
            _unitOfWork,
            _storage,
            Substitute.For<IVoiceCatalogDirectory>(),
            Substitute.For<IVoiceCloneRequestQueue>(),
            Substitute.For<IVoicePreviewQueue>(),
            Substitute.For<ILogger<VoiceProfileService>>());
    }

    private void OwnedProfile(string displayName = "My Vietnamese Voice", DateTime? deletedAt = null) =>
        _profiles.GetByUserIdAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new VoiceProfile
                {
                    Id = _profileId,
                    UserId = _userId,
                    DisplayName = displayName,
                    Language = "vi-VN",
                    Status = "active",
                    IsActive = true,
                    DeletedAt = deletedAt,
                },
            });

    private void StoredSample(string fileUrl = "user/profile.m4a", bool containsRawAudio = true) =>
        _samples.FindAsync(
                Arg.Any<Expression<Func<VoiceSample, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<VoiceSample>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    VoiceProfileId = _profileId,
                    FileUrl = fileUrl,
                    ContainsRawAudio = containsRawAudio,
                    CreatedAt = DateTime.UtcNow,
                },
            });

    private void NoSamples() =>
        _samples.FindAsync(
                Arg.Any<Expression<Func<VoiceSample, bool>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<VoiceSample>());

    [Fact]
    public async Task The_owner_gets_the_bytes_they_uploaded()
    {
        OwnedProfile();
        StoredSample();

        var result = await _service.GetSampleAsync(_userId, _profileId);

        Assert.True(result.IsSuccess);
        Assert.Equal(Recording, result.Value!.Content);
    }

    /// <summary>
    /// NotFound, not Forbidden. "That exists but is not yours" is how somebody walks a list of
    /// profile ids and learns which ones are real.
    /// </summary>
    [Fact]
    public async Task Somebody_elses_profile_is_not_found_rather_than_refused()
    {
        _profiles.GetByUserIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<VoiceProfile>());
        StoredSample();

        var result = await _service.GetSampleAsync(Guid.NewGuid(), _profileId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
        await _storage.DidNotReceive().ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_deleted_profile_stops_answering_even_for_its_owner()
    {
        OwnedProfile(deletedAt: DateTime.UtcNow);
        StoredSample();

        var result = await _service.GetSampleAsync(_userId, _profileId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
        await _storage.DidNotReceive().ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// contains_raw_audio goes false when a sample has been reduced to its embedding, and the row
    /// outlives the file. Reading the path anyway would be asking storage for something deliberately
    /// discarded.
    /// </summary>
    [Fact]
    public async Task A_sample_whose_audio_was_discarded_is_not_read_from_storage()
    {
        OwnedProfile();
        StoredSample(containsRawAudio: false);

        var result = await _service.GetSampleAsync(_userId, _profileId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidState, result.ErrorCode);
        await _storage.DidNotReceive().ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_library_pick_has_no_recording_and_says_so()
    {
        // Choosing a catalogue voice creates a profile with no sample behind it. That is not an
        // error state, so it must not read as one — but there is nothing to play.
        OwnedProfile();
        NoSamples();

        var result = await _service.GetSampleAsync(_userId, _profileId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidState, result.ErrorCode);
    }

    [Theory]
    [InlineData("user/profile.m4a", "audio/mp4")]
    [InlineData("user/profile.wav", "audio/wav")]
    [InlineData("user/profile.webm", "audio/webm")]
    [InlineData("user/profile.mp3", "audio/mpeg")]
    public async Task The_content_type_matches_what_was_uploaded(string fileUrl, string expected)
    {
        // A browser will not play an m4a announced as audio/wav, and the upload path already
        // decided the format — this is reversing that decision, not guessing at it.
        OwnedProfile();
        StoredSample(fileUrl);

        var result = await _service.GetSampleAsync(_userId, _profileId);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value!.ContentType);
    }
}

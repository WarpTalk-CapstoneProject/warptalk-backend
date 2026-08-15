using Microsoft.Extensions.Logging;
using NSubstitute;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.AuthService.Tests;

/// <summary>
/// WT-396 — collecting a clone the AI side has finished.
///
/// Uploading used to end at "bytes in a bucket, row marked active". Nothing could turn them into
/// a voice, so the profile was listed as ready and every dub still came back in a stock
/// catalogue voice. Cloning needs the Cartesia key, which this service deliberately does not
/// hold, so the work happens on the AI side and the answer comes back through Redis.
///
/// These pin the collecting half: a finished clone becomes usable, a failed one says so instead
/// of pretending, and neither can stop somebody seeing their own profiles.
/// </summary>
public class UploadCloneHandoffTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IVoiceProfileRepository _profiles = Substitute.For<IVoiceProfileRepository>();
    private readonly IVoiceCloneRequestQueue _queue = Substitute.For<IVoiceCloneRequestQueue>();
    private readonly VoiceProfileService _service;

    private readonly Guid _userId = Guid.NewGuid();
    private readonly VoiceProfile _pending;

    public UploadCloneHandoffTests()
    {
        _pending = new VoiceProfile
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            DisplayName = "My voice",
            Language = "vi",
            Status = "active",
            IsActive = true,
            EmbeddingRef = null,
        };

        _unitOfWork.VoiceProfileRepository.Returns(_profiles);
        _profiles.GetByUserIdAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new List<VoiceProfile> { _pending });

        _service = new VoiceProfileService(
            _unitOfWork,
            Substitute.For<IVoiceSampleStorage>(),
            Substitute.For<IVoiceCatalogDirectory>(),
            _queue,
            Substitute.For<ILogger<VoiceProfileService>>());
    }

    private void OutcomeIs(VoiceCloneOutcome? outcome) =>
        _queue.TakeOutcomeAsync(_pending.Id, Arg.Any<CancellationToken>()).Returns(outcome);

    [Fact]
    public async Task AFinishedCloneMakesTheProfileUsable()
    {
        OutcomeIs(new VoiceCloneOutcome("cartesia-voice-abc", "cartesia", null));

        await _service.GetProfilesAsync(_userId);

        Assert.Equal("cartesia-voice-abc", _pending.EmbeddingRef);
        Assert.Equal("active", _pending.Status);
        Assert.True(_pending.IsActive);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedCloneSaysSoRatherThanLookingReady()
    {
        // The whole of WT-396 in one assertion. A profile left marked active with no voice
        // behind it is what let somebody discover in a meeting that their upload never worked.
        OutcomeIs(new VoiceCloneOutcome(null, null, "the recording was too short"));

        await _service.GetProfilesAsync(_userId);

        Assert.Null(_pending.EmbeddingRef);
        Assert.Equal("clone_failed", _pending.Status);
        Assert.False(_pending.IsActive);
    }

    [Fact]
    public async Task AProfileStillBeingWorkedOnIsLeftExactlyAsItWas()
    {
        OutcomeIs(null);

        await _service.GetProfilesAsync(_userId);

        Assert.Null(_pending.EmbeddingRef);
        Assert.Equal("active", _pending.Status);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AProfileThatAlreadyHasAVoiceIsNotAskedAbout()
    {
        // Asking again would take an answer meant for nobody and cost a round trip per profile
        // per page load, forever, for a set that never changes.
        _pending.EmbeddingRef = "already-cloned";

        await _service.GetProfilesAsync(_userId);

        await _queue.DidNotReceive().TakeOutcomeAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADeletedProfileIsNotResurrectedByALateAnswer()
    {
        _pending.DeletedAt = DateTime.UtcNow;
        OutcomeIs(new VoiceCloneOutcome("cartesia-voice-abc", "cartesia", null));

        await _service.GetProfilesAsync(_userId);

        Assert.Null(_pending.EmbeddingRef);
        await _queue.DidNotReceive().TakeOutcomeAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnreachableQueueStillLetsSomebodySeeTheirProfiles()
    {
        // Redis being down must not make the Voice Profiles page fail. The clone stays pending,
        // which is a state the page already renders.
        _queue.TakeOutcomeAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<VoiceCloneOutcome?>(_ => throw new InvalidOperationException("redis is down"));

        var result = await _service.GetProfilesAsync(_userId);

        Assert.True(result.IsSuccess, "Redis being down took the Voice Profiles page down with it");
        Assert.Single(result.Value!);
    }
}

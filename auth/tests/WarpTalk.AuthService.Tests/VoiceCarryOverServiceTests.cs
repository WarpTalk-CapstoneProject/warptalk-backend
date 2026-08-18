using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;

namespace WarpTalk.AuthService.Tests;

/// <summary>
/// Keeping a voice the AI side cloned during a meeting (WT-B).
///
/// The producer already renamed the voice out of the orphan sweep's sights before announcing it,
/// and that single fact drives most of what is pinned here: from this point on, NOTHING else will
/// ever clean that voice up. Every path that declines to store it has to destroy it instead, or
/// promoting clones simply moves the leak the sweep was built to close into the one place the
/// sweep is told never to look.
/// </summary>
public class VoiceCarryOverServiceTests
{
    private static readonly Guid User = Guid.NewGuid();

    private readonly IVoiceProfileRepository _profiles = Substitute.For<IVoiceProfileRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IVoiceCarryOverQueue _queue = Substitute.For<IVoiceCarryOverQueue>();
    private readonly IVoiceConsentService _consent = Substitute.For<IVoiceConsentService>();
    private readonly VoiceCarryOverService _service;
    private readonly List<VoiceProfile> _added = new();

    public VoiceCarryOverServiceTests()
    {
        _unitOfWork.VoiceProfileRepository.Returns(_profiles);
        _profiles.When(r => r.Add(Arg.Any<VoiceProfile>()))
            .Do(call => _added.Add(call.Arg<VoiceProfile>()));
        _consent.HasActiveConsentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        _service = new VoiceCarryOverService(
            _unitOfWork, _queue, _consent, NullLogger<VoiceCarryOverService>.Instance);
    }

    private static VoiceCarryOverMessage Announcement(string voiceId, decimal? score = 0.8m) =>
        new("1-0", User, "vi", voiceId, score);

    private void ExistingIs(VoiceProfile? profile) =>
        _profiles.GetAutoCloneAsync(User, "vi", Arg.Any<CancellationToken>()).Returns(profile);

    private static VoiceProfile Stored(string voiceId, decimal? score) => new()
    {
        Id = Guid.NewGuid(),
        UserId = User,
        Language = "vi",
        Provider = "cartesia",
        EmbeddingRef = voiceId,
        Status = "active",
        Source = VoiceProfileSources.InMeeting,
        QualityScore = score,
        IsActive = true,
    };

    private Task<int> DeletionsRequested(string voiceId) =>
        Task.FromResult(_queue.ReceivedCalls().Count(c =>
            c.GetMethodInfo().Name == nameof(IVoiceCarryOverQueue.RequestDeletionAsync)
            && (string)c.GetArguments()[0]! == voiceId));

    [Fact]
    public async Task AFirstCaptureBecomesAProfileTheyOwn()
    {
        ExistingIs(null);

        await _service.ApplyAsync(Announcement("voice-1", 0.81m));

        var created = Assert.Single(_added);
        Assert.Equal("voice-1", created.EmbeddingRef);
        Assert.Equal(VoiceProfileSources.InMeeting, created.Source);
        Assert.Equal(0.81m, created.QualityScore);
        Assert.Equal("vi", created.Language);
    }

    [Fact]
    public async Task AnUnmeasuredCaptureIsStoredWithNoScoreRatherThanAZero()
    {
        // Zero grades as the worst possible sample and would invite replacement by anything at
        // all. "Not measured" has to survive as null all the way into the column.
        ExistingIs(null);

        await _service.ApplyAsync(Announcement("voice-1", null));

        Assert.Null(Assert.Single(_added).QualityScore);
    }

    [Fact]
    public async Task TheSameAnnouncementArrivingTwiceChangesNothing()
    {
        // The consumer acknowledges only after committing, so a crash in between redelivers.
        // Landing twice must not create a second row or destroy the voice already in use.
        ExistingIs(Stored("voice-1", 0.81m));

        await _service.ApplyAsync(Announcement("voice-1", 0.81m));

        Assert.Empty(_added);
        Assert.Equal(0, await DeletionsRequested("voice-1"));
    }

    [Fact]
    public async Task ABetterCaptureReplacesTheStoredOneAndDestroysTheLoser()
    {
        var existing = Stored("old-voice", 0.50m);
        ExistingIs(existing);

        await _service.ApplyAsync(Announcement("new-voice", 0.90m));

        Assert.Equal("new-voice", existing.EmbeddingRef);
        Assert.Equal(0.90m, existing.QualityScore);
        Assert.Equal(1, await DeletionsRequested("old-voice"));
    }

    [Fact]
    public async Task ACaptureThatIsNotAnImprovementIsDestroyedRatherThanIgnored()
    {
        // The leak this test exists for. The producer already renamed the announced voice to the
        // `profile-` prefix, so the orphan sweep will never collect it. Declining to store it and
        // walking away would strand it in the account forever.
        var existing = Stored("good-voice", 0.90m);
        ExistingIs(existing);

        await _service.ApplyAsync(Announcement("worse-voice", 0.40m));

        Assert.Equal("good-voice", existing.EmbeddingRef);
        Assert.Equal(1, await DeletionsRequested("worse-voice"));
    }

    [Fact]
    public async Task AnUnmeasuredIncumbentLosesToAMeasuredChallenger()
    {
        // Unmeasured is not a claim to be good. Refusing to replace it would strand people on
        // rows written before scores existed.
        var existing = Stored("unmeasured-voice", null);
        ExistingIs(existing);

        await _service.ApplyAsync(Announcement("measured-voice", 0.30m));

        Assert.Equal("measured-voice", existing.EmbeddingRef);
        Assert.Equal(1, await DeletionsRequested("unmeasured-voice"));
    }

    [Fact]
    public async Task AVoiceIsNeverKeptForSomebodyWhoHasWithdrawnConsent()
    {
        // The producer checked consent when it captured the audio; this runs afterwards, which is
        // exactly when somebody who changed their mind would have withdrawn it. Storing it anyway
        // would make the withdrawal a lie in the most durable form available — a permanent row.
        _consent.HasActiveConsentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        ExistingIs(null);

        await _service.ApplyAsync(Announcement("voice-1", 0.95m));

        Assert.Empty(_added);
        Assert.Equal(1, await DeletionsRequested("voice-1"));
    }

    [Fact]
    public async Task TheReplacementIsCommittedBeforeTheOldVoiceIsDestroyed()
    {
        // The other order can destroy a voice the row still names — a profile pointing at an id
        // Cartesia has never heard of, dubbing the person as a stranger while looking correct.
        ExistingIs(Stored("old-voice", 0.10m));

        var committed = false;
        _unitOfWork.When(u => u.SaveChangesAsync(Arg.Any<CancellationToken>()))
            .Do(_ => committed = true);
        _queue.When(q => q.RequestDeletionAsync(
                "old-voice", Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => Assert.True(committed, "the old voice was destroyed before the row was committed"));

        await _service.ApplyAsync(Announcement("new-voice", 0.90m));

        Assert.Equal(1, await DeletionsRequested("old-voice"));
    }
}

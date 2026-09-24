using System.Diagnostics.Metrics;
using FluentAssertions;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Helpers;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// The live-meeting success rate's counters. Each test listens only to its own instrument names
/// on the service meter, the same meter AddWarpTalkObservability exports; an instrument created on
/// any other meter would be counted here and exported nowhere.
/// </summary>
public sealed class MeetingLifecycleMetricsTests
{
    private sealed class Recorded : IDisposable
    {
        private readonly MeterListener _listener = new();
        public List<(string Name, double Value, Dictionary<string, string?> Tags)> Measurements { get; } = new();

        public Recorded(params string[] instruments)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == MeetingLifecycleMetrics.MeterName && instruments.Contains(instrument.Name))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i.Name, v, tags));
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i.Name, v, tags));
            _listener.Start();
        }

        public void Collect() => _listener.RecordObservableInstruments();

        private void Add(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var map = new Dictionary<string, string?>();
            foreach (var tag in tags) map[tag.Key] = tag.Value?.ToString();
            lock (Measurements) Measurements.Add((name, value, map));
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public void TheMeterIsTheServiceName_WhichIsWhatObservabilitySubscribesTo()
    {
        MeetingLifecycleMetrics.MeterName.Should().Be("warptalk-translation-room");
    }

    [Theory]
    [InlineData(1, true, false)]
    [InlineData(2, false, false)]
    [InlineData(2, true, true)]
    [InlineData(5, true, true)]
    public void ReachedLiveNeedsTwoPeopleAndACaption(int everJoined, bool caption, bool expected)
    {
        MeetingLifecycleMetrics.ReachedLive(everJoined, caption).Should().Be(expected);
    }

    [Fact]
    public void AnEndingCarriesItsReasonAndWhetherItReachedLive_AndItsDuration()
    {
        using var recorded = new Recorded("meeting.ended", "meeting.duration");

        MeetingLifecycleMetrics.RecordEnded("abandoned", reachedLive: true, TimeSpan.FromMinutes(42));

        // Contain, not ContainSingle: the meter is static and other test classes run in parallel.
        recorded.Measurements.Should().Contain(m =>
            m.Name == "meeting.ended"
            && m.Tags["end_reason"] == "abandoned"
            && m.Tags["reached_live"] == "true");
        recorded.Measurements.Should().Contain(m =>
            m.Name == "meeting.duration" && m.Value == 42 * 60 && m.Tags["reached_live"] == "true");
    }

    [Fact]
    public void EndReasonScopesNestAndRestore()
    {
        MeetingLifecycleMetrics.CurrentEndReason.Should().Be(MeetingLifecycleMetrics.EndReasonHost);

        using (MeetingLifecycleMetrics.EndReasonScope(MeetingLifecycleMetrics.EndReasonAbandoned))
        {
            MeetingLifecycleMetrics.CurrentEndReason.Should().Be(MeetingLifecycleMetrics.EndReasonAbandoned);
            using (MeetingLifecycleMetrics.EndReasonScope(MeetingLifecycleMetrics.EndReasonExpired))
            {
                MeetingLifecycleMetrics.CurrentEndReason.Should().Be(MeetingLifecycleMetrics.EndReasonExpired);
            }

            MeetingLifecycleMetrics.CurrentEndReason.Should().Be(MeetingLifecycleMetrics.EndReasonAbandoned);
        }

        MeetingLifecycleMetrics.CurrentEndReason.Should().Be(MeetingLifecycleMetrics.EndReasonHost);
    }

    [Fact]
    public void AStaleSweepSnapshotIsWithheld_SoAFormerLockHolderCannotReportOldRooms()
    {
        using var recorded = new Recorded("meeting.live_rooms", "meeting.occupied_rooms");

        // Values no other test writes: the snapshot is static and the sweep tests run in parallel.
        MeetingLifecycleMetrics.RecordRoomSnapshot(9_107, 9_103, DateTimeOffset.UtcNow - TimeSpan.FromHours(1));
        recorded.Collect();
        recorded.Measurements.Should().NotContain(m => m.Value == 9_107 || m.Value == 9_103);

        MeetingLifecycleMetrics.RecordRoomSnapshot(9_107, 9_103, DateTimeOffset.UtcNow);
        recorded.Collect();
        recorded.Measurements.Should().Contain(m => m.Name == "meeting.live_rooms" && m.Value == 9_107);
        recorded.Measurements.Should().Contain(m => m.Name == "meeting.occupied_rooms" && m.Value == 9_103);
    }

    [Fact]
    public void TheStartAndCaptionMarkersAreTheSameKeyForEverySpellingOfTheRoomId()
    {
        var id = Guid.Parse("7f0c1a52-6a0e-4c1c-9b8e-3d2f1a4b5c6d");

        MeetingLifecycleKeys.StartedAt(id).Should().Be(MeetingLifecycleKeys.StartedAt(id.ToString().ToUpperInvariant()));
        MeetingLifecycleKeys.FirstCaptionAt(id).Should().Be(
            "translationRoom:7f0c1a52-6a0e-4c1c-9b8e-3d2f1a4b5c6d:metrics:first_caption_at");
    }
}

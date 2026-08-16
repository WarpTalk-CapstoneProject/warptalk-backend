using System;
using System.Linq;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.StateMachines;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.StateMachines;

/// <summary>
/// WT-429. Every event the AI workers publish onto translationRoom:system_events must parse.
///
/// 497 messages sat in that stream's dead-letter queue, all with the same error — "Unknown event
/// type." — because two names the workers publish were absent from
/// <see cref="AudioRoutingEventType"/>: stt_unavailable (381) and final_chunk_processed (116),
/// across 83 rooms over two weeks.
///
/// The absence was not random. The enum already carried stt_recovered, the RECOVERY half of the
/// pair, and both siblings tts_unavailable and audio_unavailable — so the one signal saying
/// transcription had stopped was the one nothing could read.
/// </summary>
public sealed class UnknownSystemEventContractTests
{
    /// <summary>
    /// The literal strings stt_worker and tts_worker put on the wire. Spelled out rather than
    /// derived from the enum, so a rename on the C# side that breaks the producers fails here
    /// instead of silently filling the DLQ again.
    /// </summary>
    [Theory]
    [InlineData("stt_unavailable")]
    [InlineData("final_chunk_processed")]
    [InlineData("tts_unavailable")]
    [InlineData("audio_unavailable")]
    [InlineData("participant_language_changed")]
    public void EveryEventTypeTheWorkersPublish_Parses(string publishedName)
    {
        Assert.True(
            Enum.TryParse<AudioRoutingEventType>(publishedName, ignoreCase: true, out _),
            $"'{publishedName}' is published onto translationRoom:system_events but does not "
            + "parse, so the consumer dead-letters it after three attempts.");
    }

    [Fact]
    public void EveryUnavailableSignalHasARecoveryPartner()
    {
        // The defect in one sentence: stt_recovered existed and stt_unavailable did not. A latch
        // with only one half is either a signal nobody can raise or one nobody can clear.
        var names = Enum.GetNames<AudioRoutingEventType>().ToHashSet(StringComparer.Ordinal);

        foreach (var unavailable in names.Where(n => n.EndsWith("_unavailable", StringComparison.Ordinal)))
        {
            var stem = unavailable[..^"_unavailable".Length];
            Assert.True(
                names.Contains($"{stem}_recovered"),
                $"{unavailable} can be raised but {stem}_recovered does not exist to clear it.");
        }
    }

    [Fact]
    public void AnInformationalEventIsAcceptedWithoutMovingTheRoute()
    {
        // final_chunk_processed reports progress. It must not be an error — that is what filled
        // the DLQ — and it must not move a route either.
        var machine = new AudioRouteStateMachine();

        var result = machine.GetNextState(
            AudioRouteStatus.BROADCASTING,
            AudioRoutingEventType.final_chunk_processed);

        Assert.True(result.IsSuccess);
        Assert.Equal(AudioRouteStatus.BROADCASTING, result.Value);
    }
}

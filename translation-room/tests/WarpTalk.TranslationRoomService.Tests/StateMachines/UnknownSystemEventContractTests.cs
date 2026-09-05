using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
///
/// SECOND ROUND, 27 Aug 2026. The fix above did not hold. tts_worker started publishing
/// voice_clone_ready on 16 Aug — the day after WT-429 shipped — and it dead-lettered 19 more times
/// with the identical error before anyone looked. The hand-written list below could not have caught
/// it: a list of the names that existed when it was written catches a RENAME and never an ADDITION.
/// <see cref="EveryEventTypeInTheWorkerSources_Parses"/> is the answer to that, derived from the
/// producer sources rather than restated here.
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
    [InlineData("voice_clone_ready")]
    [InlineData("audio_unavailable")]
    [InlineData("participant_language_changed")]
    public void EveryEventTypeTheWorkersPublish_Parses(string publishedName)
    {
        Assert.True(
            Enum.TryParse<AudioRoutingEventType>(publishedName, ignoreCase: true, out _),
            $"'{publishedName}' is published onto translationRoom:system_events but does not "
            + "parse, so the consumer dead-letters it after three attempts.");
    }

    /// <summary>
    /// The list above, but read out of warptalk-ai instead of typed by hand — so a name added to a
    /// worker after this file was last edited fails here rather than in production.
    ///
    /// Skipped when the sibling repository is not on disk. warptalk-backend's own CI checks out
    /// only itself, so this guard runs on developer machines and in the release workflow's
    /// four-repo layout, NOT in backend CI. Making backend CI clone warptalk-ai would buy teeth at
    /// the price of a cross-repo branch deadlock this project has already been bitten by, so the
    /// hand-written Theory above stays as the CI-visible half.
    /// </summary>
    [Fact]
    public void EveryEventTypeInTheWorkerSources_Parses()
    {
        var workerRoot = FindAiRepositoryOrNull();
        if (workerRoot is null)
        {
            // Nothing to read, so nothing to assert. xUnit 2.9.3 has no dynamic skip, and this
            // passing vacuously is exactly why EveryEventTypeTheWorkersPublish_Parses above is
            // kept: that one has teeth wherever this one does not.
            return;
        }

        // publish_system_event(room_id=..., event_type="voice_clone_ready", payload=...)
        var publishedName = new Regex(
            @"event_type\s*[=:]\s*""(?<name>[a-z][a-z0-9_]*)""",
            RegexOptions.Compiled | RegexOptions.Singleline);

        var published = Directory
            .EnumerateFiles(workerRoot, "*.py", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}.venv{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(file => publishedName.Matches(File.ReadAllText(file)).Select(m => m.Groups["name"].Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        // A scan that finds nothing would pass this test while proving nothing — the exact shape of
        // a guard that has quietly stopped guarding.
        Assert.NotEmpty(published);

        var unparseable = published
            .Where(name => !Enum.TryParse<AudioRoutingEventType>(name, ignoreCase: true, out _))
            .ToList();

        Assert.True(
            unparseable.Count == 0,
            $"warptalk-ai publishes {string.Join(", ", unparseable)} onto "
            + "translationRoom:system_events, but AudioRoutingEventType has no such member — the "
            + "consumer will dead-letter every one with \"Unknown event type.\" Add the member, and "
            + "decide in AudioRouteStateMachine whether it moves a route or is informational.");
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

    /// <summary>
    /// Informational events report progress. They must not be an error — that is what filled the
    /// DLQ — and they must not move a route either.
    ///
    /// voice_clone_ready is here rather than beside voice_clone_recovered deliberately: it carries
    /// a speakerId the processor cannot narrow to one route, so accepting it as a transition would
    /// pull EVERY route in the room out of its voice-clone fallback.
    /// </summary>
    [Theory]
    [InlineData(AudioRoutingEventType.final_chunk_processed)]
    [InlineData(AudioRoutingEventType.voice_clone_ready)]
    public void AnInformationalEventIsAcceptedWithoutMovingTheRoute(AudioRoutingEventType eventType)
    {
        var machine = new AudioRouteStateMachine();

        var result = machine.GetNextState(AudioRouteStatus.BROADCASTING, eventType);

        Assert.True(result.IsSuccess);
        Assert.Equal(AudioRouteStatus.BROADCASTING, result.Value);
    }

    /// <summary>
    /// A route already in STANDARD_VOICE — the voice-clone fallback — is the case that would break
    /// if voice_clone_ready were ever wired up as a recovery. It must sit still.
    /// </summary>
    [Fact]
    public void VoiceCloneReady_DoesNotLiftARouteOutOfItsFallback()
    {
        var machine = new AudioRouteStateMachine();

        var result = machine.GetNextState(
            AudioRouteStatus.STANDARD_VOICE,
            AudioRoutingEventType.voice_clone_ready);

        Assert.True(result.IsSuccess);
        Assert.Equal(AudioRouteStatus.STANDARD_VOICE, result.Value);
    }

    /// <summary>The warptalk-ai worker sources, or null when the repository is not on disk.</summary>
    private static string? FindAiRepositoryOrNull()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "warptalk-ai");
                if (File.Exists(Path.Combine(candidate, "shared", "redis_client.py")))
                    return candidate;
                directory = directory.Parent;
            }
        }

        return null;
    }
}

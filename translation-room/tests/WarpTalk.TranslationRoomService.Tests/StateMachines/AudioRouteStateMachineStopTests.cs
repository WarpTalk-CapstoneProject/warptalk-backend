using FluentAssertions;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.StateMachines;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.StateMachines;

/// <summary>
/// Stopping translation and pausing the room are different acts, and the state machine is where
/// the difference is decided. PAUSED is the AI workers' signal to ignore a room's microphone
/// altogether — correct for a pause, and wrong for "stop translating but keep transcribing",
/// which is why translation_stopped lands on READY instead.
/// </summary>
public class AudioRouteStateMachineStopTests
{
    private readonly AudioRouteStateMachine _stateMachine = new();

    [Theory]
    [InlineData(AudioRouteStatus.BROADCASTING)]
    [InlineData(AudioRouteStatus.SPEECH_DELAYED)]
    [InlineData(AudioRouteStatus.TRANSLATION_DELAYED)]
    [InlineData(AudioRouteStatus.VOICE_DELAYED)]
    [InlineData(AudioRouteStatus.STANDARD_VOICE)]
    [InlineData(AudioRouteStatus.CAPTION_ONLY)]
    [InlineData(AudioRouteStatus.PAUSED)]
    public void TranslationStopped_ReturnsAnyRunningRouteToReady(AudioRouteStatus current)
    {
        var result = _stateMachine.GetNextState(current, AudioRoutingEventType.translation_stopped);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(AudioRouteStatus.READY);
    }

    /// <summary>
    /// Start after Stop is the same transition as the first Start ever — that is the point of
    /// landing on READY rather than inventing a second resting state.
    /// </summary>
    [Fact]
    public void AStoppedRouteCanBeStartedAgain()
    {
        var stopped = _stateMachine.GetNextState(
            AudioRouteStatus.BROADCASTING,
            AudioRoutingEventType.translation_stopped);

        var restarted = _stateMachine.GetNextState(
            stopped.Value,
            AudioRoutingEventType.session_starts);

        restarted.IsSuccess.Should().BeTrue();
        restarted.Value.Should().Be(AudioRouteStatus.BROADCASTING);
    }

    [Fact]
    public void TranslationStopped_DoesNotDisturbARouteThatIsFinalising()
    {
        var result = _stateMachine.GetNextState(
            AudioRouteStatus.SAVING_OUTPUTS,
            AudioRoutingEventType.translation_stopped);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void TranslationStopped_LeavesACompletedRouteCompleted()
    {
        var result = _stateMachine.GetNextState(
            AudioRouteStatus.COMPLETED,
            AudioRoutingEventType.translation_stopped);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(AudioRouteStatus.COMPLETED);
    }
}

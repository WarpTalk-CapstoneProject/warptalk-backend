using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using WarpTalk.TranslationRoomService.Application.EventHandlers;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.StateMachines;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// S8 — PENDING was a dead end, and PENDING is what the client renders as "Waiting".
///
/// GenerateRoutesAsync creates every route at PENDING. The only transition out of PENDING is
/// <c>config_ready</c>, and nothing in this repository has ever emitted that event.
/// <c>session_starts</c> is only accepted from READY, so it was rejected on every freshly
/// generated route, and <c>telemetry_state_updated</c> is rejected outright because PENDING is
/// not a streaming state. A route therefore sat at PENDING for the whole meeting no matter what
/// happened — whether anyone spoke or not.
///
/// These tests pin the transition chain the fix depends on: the state table was already correct,
/// what was missing was a producer (see TranslationRoomService.PublishRouteReadinessAsync).
/// </summary>
public class RouteReadinessTests
{
    private readonly AudioRouteTransitionProcessor _processor = new(
        new AudioRouteStateMachine(),
        NullLogger<AudioRouteTransitionProcessor>.Instance);

    private static TranslationRoomAudioRoute PendingRoute() => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = Guid.NewGuid(),
        SourceParticipantId = Guid.NewGuid(),
        TargetParticipantId = Guid.NewGuid(),
        SourceLanguage = "en",
        TargetLanguage = "vi",
        Status = AudioRouteStatus.PENDING.ToString(),
    };

    [Fact]
    public void SessionStartsAlone_CannotMoveAFreshRouteOffPending()
    {
        // The bug, pinned: this is exactly what StartTranslationRoomAsync used to do on its own.
        var route = PendingRoute();

        var changed = _processor.ProcessTransition(route, AudioRoutingEventType.session_starts);

        changed.Should().BeFalse();
        route.Status.Should().Be(AudioRouteStatus.PENDING.ToString());
    }

    [Fact]
    public void ConfigReadyThenSessionStarts_ReachesBroadcasting()
    {
        var route = PendingRoute();

        _processor.ProcessTransition(route, AudioRoutingEventType.config_ready).Should().BeTrue();
        route.Status.Should().Be(AudioRouteStatus.READY.ToString());

        _processor.ProcessTransition(route, AudioRoutingEventType.session_starts).Should().BeTrue();
        route.Status.Should().Be(AudioRouteStatus.BROADCASTING.ToString());
        route.StartedAt.Should().NotBe(default);
    }

    [Fact]
    public void TelemetryCannotReachAPendingRoute()
    {
        // Why "just publish telemetry" was never going to unstick this: a status update is only
        // accepted from a streaming state, so no amount of speech helps a PENDING route.
        var route = PendingRoute();

        var changed = _processor.ProcessTransition(
            route, AudioRoutingEventType.telemetry_state_updated, "{\"status\":\"BROADCASTING\"}");

        changed.Should().BeFalse();
        route.Status.Should().Be(AudioRouteStatus.PENDING.ToString());
    }

    [Theory]
    [InlineData(AudioRoutingEventType.config_ready)]
    [InlineData(AudioRoutingEventType.session_starts)]
    public void ReadinessEventsAreIdempotent_OnAnAlreadyBroadcastingRoute(AudioRoutingEventType eventType)
    {
        // The readiness pair is re-emitted on every late join and every restart, so replaying it
        // over a live route must be a no-op rather than a state change.
        var route = PendingRoute();
        route.Status = AudioRouteStatus.BROADCASTING.ToString();

        var changed = _processor.ProcessTransition(route, eventType);

        changed.Should().BeFalse();
        route.Status.Should().Be(AudioRouteStatus.BROADCASTING.ToString());
    }

    [Fact]
    public void ReadinessDoesNotResurrectAPausedRoute()
    {
        var route = PendingRoute();
        route.Status = AudioRouteStatus.PAUSED.ToString();

        _processor.ProcessTransition(route, AudioRoutingEventType.config_ready).Should().BeFalse();
        _processor.ProcessTransition(route, AudioRoutingEventType.session_starts).Should().BeFalse();
        route.Status.Should().Be(AudioRouteStatus.PAUSED.ToString());
    }
}

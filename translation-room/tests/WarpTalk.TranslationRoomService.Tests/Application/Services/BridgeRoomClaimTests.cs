using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;
using RoomService = WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// One shared EXTERNAL_BRIDGE room per Google Meet code (per workspace), one audio capturer.
///
/// Driven through the real TranslationRoomService create and join paths against an in-memory
/// store that behaves like the database where it matters: rows become visible only on
/// SaveChanges, and the (workspace_id, external_meeting_code) partial unique index rejects a
/// second open bridge room with SQLSTATE 23505, exactly as Postgres would.
/// </summary>
public class BridgeRoomClaimTests
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private const string MeetCode = "abc-defg-hij";

    private readonly FakeBridgeStore _store = new();
    private DateTime _now = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private RoomService Service() => _store.BuildService(() => _now);

    private static ClaimBridgeRoomRequest Claim(Guid workspaceId, string meetCode = MeetCode) =>
        new(workspaceId, meetCode, SourceLanguage: "vi", TargetLanguages: ["vi", "en"], ExternalMeetingLanguage: "en");

    private Guid Member(Guid workspaceId)
    {
        var userId = Guid.NewGuid();
        _store.Members.Add((workspaceId, userId));
        return userId;
    }

    [Fact]
    public async Task Claim_CreatesTheRoom_AndMakesTheCreatorTheCapturer()
    {
        var alice = Member(WorkspaceA);

        var result = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA, "  https://meet.google.com/ABC-DEFG-HIJ?authuser=0 "), alice);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Created.Should().BeTrue();
        result.Value.BridgeRole.Should().Be(BridgeRoomConstants.RoleCapturer);
        result.Value.Room.TranslationRoomType.Should().Be(TranslationRoomTypes.ExternalBridge);
        result.Value.Room.ExternalMeetingCode.Should().Be(MeetCode);
        result.Value.Room.ExternalMeetingUrl.Should().Be("https://meet.google.com/abc-defg-hij");
        result.Value.Room.BridgeCapturerUserId.Should().Be(alice);

        var room = _store.Rooms.Single();
        room.HostId.Should().Be(alice);
        room.BridgeCapturerUserId.Should().Be(alice);
        room.BridgeCapturerHeartbeatAt.Should().Be(_now);
        _store.Participants.Should().Contain(p => p.UserId == TranslationRoomConstants.ExternalBridgeParticipantUserId);
    }

    [Fact]
    public async Task Claim_BySecondMemberOfTheSameWorkspace_ReusesTheRoom_AsAMember()
    {
        var alice = Member(WorkspaceA);
        var bob = Member(WorkspaceA);
        var first = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), alice);

        var second = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), bob);

        second.IsSuccess.Should().BeTrue(second.Error);
        second.Value!.Created.Should().BeFalse();
        second.Value.Room.Id.Should().Be(first.Value!.Room.Id);
        second.Value.BridgeRole.Should().Be(BridgeRoomConstants.RoleMember);
        second.Value.Participant!.UserId.Should().Be(bob);
        second.Value.Participant.Status.Should().Be(TranslationRoomParticipantStatuses.Connected);
        // Bridge rule: a person hears the call in their own language.
        second.Value.Participant.SpeakLanguage.Should().Be("vi");
        second.Value.Participant.ListenLanguage.Should().Be("vi");
        _store.Rooms.Should().ContainSingle();
        _store.Rooms.Single().BridgeCapturerUserId.Should().Be(alice);
    }

    [Fact]
    public async Task Claim_RaceOnTheUniqueIndex_LeavesOneRoom_AndTheLoserJoinsItAsAMember()
    {
        var alice = Member(WorkspaceA);
        var bob = Member(WorkspaceA);
        await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), alice);

        // Bob's lookup ran before Alice's insert committed: he saw no room and tries to create one.
        _store.StaleLookupsRemaining = 1;
        var bobs = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), bob);

        bobs.IsSuccess.Should().BeTrue(bobs.Error);
        _store.UniqueViolationsRaised.Should().Be(1, "the index, not the client, decided the race");
        _store.Rooms.Should().ContainSingle();
        bobs.Value!.Room.Id.Should().Be(_store.Rooms.Single().Id);
        bobs.Value.BridgeRole.Should().Be(BridgeRoomConstants.RoleMember);
        _store.Pending.Should().BeEmpty("the losing rows must not be re-inserted by the join's SaveChanges");
        // Exactly one stand-in: the loser's seed was discarded.
        _store.Participants.Count(p => p.UserId == TranslationRoomConstants.ExternalBridgeParticipantUserId).Should().Be(1);
    }

    [Fact]
    public async Task Claim_TwoConcurrentClaims_ProduceOneRoom()
    {
        var alice = Member(WorkspaceA);
        var bob = Member(WorkspaceA);

        // Both lookups miss: the store answers "no room" to the first two lookups.
        _store.StaleLookupsRemaining = 2;
        var results = await Task.WhenAll(
            Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), alice),
            Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), bob));

        results.Should().OnlyContain(r => r.IsSuccess);
        _store.Rooms.Should().ContainSingle();
        results.Select(r => r.Value!.Room.Id).Distinct().Should().ContainSingle();
        results.Count(r => r.Value!.BridgeRole == BridgeRoomConstants.RoleCapturer).Should().Be(1);
    }

    [Fact]
    public async Task Claim_FromAnotherWorkspace_CreatesItsOwnRoom()
    {
        var alice = Member(WorkspaceA);
        var carol = Member(WorkspaceB);
        var a = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), alice);

        var b = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceB), carol);

        b.IsSuccess.Should().BeTrue(b.Error);
        b.Value!.Created.Should().BeTrue();
        b.Value.Room.Id.Should().NotBe(a.Value!.Room.Id);
        b.Value.BridgeRole.Should().Be(BridgeRoomConstants.RoleCapturer);
        _store.Rooms.Should().HaveCount(2);
    }

    [Fact]
    public async Task Claim_ByANonMember_IsForbidden_AndCreatesNothing()
    {
        var stranger = Guid.NewGuid();

        var result = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), stranger);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        _store.Rooms.Should().BeEmpty();
    }

    [Fact]
    public async Task Claim_WithAMalformedCode_IsAValidationError()
    {
        var alice = Member(WorkspaceA);

        var result = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA, "not a meet code"), alice);

        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
    }

    [Fact]
    public async Task Claim_WhenTheWorkspaceRefusesCreation_FailsLikeCreate_ButAMemberMayStillJoinAnExistingRoom()
    {
        var alice = Member(WorkspaceA);
        var bob = Member(WorkspaceA);
        await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), alice);

        _store.DenyCreationFor.Add(bob);
        var join = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), bob);
        var create = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA, "xyz-wxyz-xyz"), bob);

        join.IsSuccess.Should().BeTrue(join.Error);
        join.Value!.BridgeRole.Should().Be(BridgeRoomConstants.RoleMember);
        create.IsSuccess.Should().BeFalse();
        create.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Claim_AfterTheCapturerWentStale_HandsTheCapturerToTheClaimer()
    {
        var alice = Member(WorkspaceA);
        var bob = Member(WorkspaceA);
        await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), alice);

        _now = _now.AddSeconds(46);
        var bobs = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), bob);

        bobs.Value!.BridgeRole.Should().Be(BridgeRoomConstants.RoleCapturer);
        bobs.Value.Room.BridgeCapturerUserId.Should().Be(bob);
        _store.Rooms.Single().BridgeCapturerUserId.Should().Be(bob);
    }

    [Fact]
    public async Task TakeOver_IsRefusedWhileTheCapturerIsLive_AndSucceedsOnceStale()
    {
        var alice = Member(WorkspaceA);
        var bob = Member(WorkspaceA);
        var room = (await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), alice)).Value!.Room;
        await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), bob);

        _now = _now.AddSeconds(30);
        var early = await Service().TakeOverBridgeCapturerAsync(room.Id, bob);
        early.ErrorCode.Should().Be(ErrorCodes.Conflict);

        // Alice keeps her lease alive...
        (await Service().HeartbeatBridgeCapturerAsync(room.Id, alice)).IsSuccess.Should().BeTrue();
        _now = _now.AddSeconds(40);
        (await Service().TakeOverBridgeCapturerAsync(room.Id, bob)).ErrorCode.Should().Be(ErrorCodes.Conflict);

        // ...then her desktop goes away.
        _now = _now.AddSeconds(10);
        var takeover = await Service().TakeOverBridgeCapturerAsync(room.Id, bob);

        takeover.IsSuccess.Should().BeTrue(takeover.Error);
        takeover.Value!.BridgeRole.Should().Be(BridgeRoomConstants.RoleCapturer);
        takeover.Value.CapturerUserId.Should().Be(bob);
        _store.Rooms.Single().BridgeCapturerUserId.Should().Be(bob);

        // The old capturer's heartbeat now answers 409: it must stop publishing the far side.
        (await Service().HeartbeatBridgeCapturerAsync(room.Id, alice)).ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    [Fact]
    public async Task TakeOver_ByANonParticipant_IsForbidden()
    {
        var alice = Member(WorkspaceA);
        var room = (await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), alice)).Value!.Room;
        _now = _now.AddMinutes(5);

        var result = await Service().TakeOverBridgeCapturerAsync(room.Id, Member(WorkspaceA));

        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Heartbeat_ByAMember_IsAConflict()
    {
        var alice = Member(WorkspaceA);
        var bob = Member(WorkspaceA);
        var room = (await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), alice)).Value!.Room;
        await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), bob);

        (await Service().HeartbeatBridgeCapturerAsync(room.Id, bob)).ErrorCode.Should().Be(ErrorCodes.Conflict);
        var own = await Service().HeartbeatBridgeCapturerAsync(room.Id, alice);
        own.IsSuccess.Should().BeTrue();
        own.Value!.CapturerHeartbeatAt.Should().Be(_now);
    }

    [Fact]
    public async Task Heartbeat_OnAnEndedRoom_IsRefused()
    {
        var alice = Member(WorkspaceA);
        var room = (await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), alice)).Value!.Room;
        _store.Rooms.Single().Status = "ENDED";

        (await Service().HeartbeatBridgeCapturerAsync(room.Id, alice)).ErrorCode.Should().Be(ErrorCodes.InvalidState);
    }

    [Fact]
    public async Task Claim_AfterTheRoomEnded_StartsAFreshRoomForTheSameCode()
    {
        var alice = Member(WorkspaceA);
        var first = (await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), alice)).Value!.Room;
        _store.Rooms.Single().Status = "ENDED";

        var again = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), alice);

        again.Value!.Created.Should().BeTrue();
        again.Value.Room.Id.Should().NotBe(first.Id);
    }

    [Fact]
    public async Task Capacity_SeatsThreeAndMoreHumans_AndTheStandInDoesNotCount()
    {
        var host = Member(WorkspaceA);
        await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), host);
        var room = _store.Rooms.Single();
        room.MaxParticipants.Should().Be(20);

        for (var i = 0; i < 4; i++)
        {
            var joined = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), Member(WorkspaceA));
            joined.IsSuccess.Should().BeTrue(joined.Error);
        }

        _store.Participants.Count(p => p.Status == TranslationRoomParticipantStatuses.Connected
            && p.UserId != TranslationRoomConstants.ExternalBridgeParticipantUserId).Should().Be(5);

        // A cap of N admits N humans: the stand-in's always-held seat is not one of them.
        room.MaxParticipants = 6;
        (await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), Member(WorkspaceA))).IsSuccess.Should().BeTrue();
        var overCap = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), Member(WorkspaceA));
        overCap.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    [Fact]
    public void Reapers_SeeALiveMember_AfterTheCapturerLeft()
    {
        var roomId = Guid.NewGuid();
        var participants = new[]
        {
            new TranslationRoomParticipant { TranslationRoomId = roomId, UserId = TranslationRoomConstants.ExternalBridgeParticipantUserId, Status = TranslationRoomParticipantStatuses.Connected },
            new TranslationRoomParticipant { TranslationRoomId = roomId, UserId = Guid.NewGuid(), Status = TranslationRoomParticipantStatuses.Left },      // the capturer
            new TranslationRoomParticipant { TranslationRoomId = roomId, UserId = Guid.NewGuid(), Status = TranslationRoomParticipantStatuses.Connected }, // a member
        };

        var people = participants.Count(RoomPresence.IsPersonInRoom);

        people.Should().Be(1);
        AbandonedRoomPolicy.Decide(people, emptySince: null, DateTime.UtcNow).Should().Be(AbandonedRoomAction.Leave);
    }

    // ---- GMCAL1001: a Meet room WarpBot files through the ordinary POST /translation-rooms ----

    /// <summary>The AI worker's payload: languages omitted (user defaults), a future slot.</summary>
    private CreateTranslationRoomRequest WarpBotCreate(
        Guid workspaceId,
        string meetUrl = "https://meet.google.com/abc-defg-hij",
        string type = TranslationRoomTypes.ExternalBridge,
        string? provider = TranslationRoomConstants.ExternalProviderGoogleMeet) =>
        new(
            WorkspaceId: workspaceId,
            Title: "Weekly sync",
            Description: null,
            TranslationRoomType: type,
            MaxParticipants: null,
            SourceLanguage: null,
            TargetLanguages: null,
            Settings: null,
            ScheduledAt: _now.AddDays(1),
            InvitedEmails: null,
            ExternalProvider: provider,
            ExternalMeetingUrl: provider is null ? null : meetUrl,
            ExternalCalendarEventId: "evt-1",
            ExternalCalendarEventUrl: "https://calendar.google.com/event?eid=evt-1");

    [Fact]
    public async Task WarpBotCreate_StampsTheMeetCode_ButNotTheCapturer()
    {
        var alice = Member(WorkspaceA);

        var result = await Service().CreateTranslationRoomAsync(WarpBotCreate(WorkspaceA), alice);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.ExternalMeetingCode.Should().Be(MeetCode);
        result.Value.Status.Should().Be(WarpTalk.TranslationRoomService.Domain.Enums.RoomStatus.SCHEDULED);
        var room = _store.Rooms.Single();
        room.ExternalMeetingCode.Should().Be(MeetCode);
        room.BridgeCapturerUserId.Should().BeNull("only a desktop claim makes someone the capturer");
        room.BridgeCapturerHeartbeatAt.Should().BeNull();
    }

    [Fact]
    public async Task WarpBotCreate_TwiceForTheSameCode_ReturnsTheFirstRoom()
    {
        var alice = Member(WorkspaceA);
        var first = await Service().CreateTranslationRoomAsync(WarpBotCreate(WorkspaceA), alice);

        var second = await Service().CreateTranslationRoomAsync(
            WarpBotCreate(WorkspaceA, "https://meet.google.com/ABC-DEFG-HIJ?authuser=0"), alice);

        second.IsSuccess.Should().BeTrue(second.Error);
        second.Value!.Id.Should().Be(first.Value!.Id);
        _store.Rooms.Should().ContainSingle();
        _store.Pending.Should().BeEmpty();
    }

    [Fact]
    public async Task WarpBotCreate_LosingTheRaceOnTheIndex_ReturnsTheWinnersRoom()
    {
        var alice = Member(WorkspaceA);
        var first = await Service().CreateTranslationRoomAsync(WarpBotCreate(WorkspaceA), alice);

        _store.StaleLookupsRemaining = 1; // the pre-check misses; the unique index decides
        var second = await Service().CreateTranslationRoomAsync(WarpBotCreate(WorkspaceA), alice);

        second.IsSuccess.Should().BeTrue(second.Error);
        second.Value!.Id.Should().Be(first.Value!.Id);
        _store.UniqueViolationsRaised.Should().Be(1);
        _store.Rooms.Should().ContainSingle();
        _store.Pending.Should().BeEmpty("the losing rows must not linger in the change tracker");
    }

    [Fact]
    public async Task WarpBotCreate_ForACodeAnotherMembersRoomHolds_IsAConflict_UntilTheyAreInIt()
    {
        var alice = Member(WorkspaceA);
        var bob = Member(WorkspaceA);
        var alices = await Service().CreateTranslationRoomAsync(WarpBotCreate(WorkspaceA), alice);

        var refused = await Service().CreateTranslationRoomAsync(WarpBotCreate(WorkspaceA), bob);
        refused.IsSuccess.Should().BeFalse();
        refused.ErrorCode.Should().Be(ErrorCodes.Conflict);

        await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), bob);
        var asParticipant = await Service().CreateTranslationRoomAsync(WarpBotCreate(WorkspaceA), bob);
        asParticipant.IsSuccess.Should().BeTrue(asParticipant.Error);
        asParticipant.Value!.Id.Should().Be(alices.Value!.Id);
        _store.Rooms.Should().ContainSingle();
    }

    [Fact]
    public async Task Claim_AfterAWarpBotScheduledRoom_JoinsThatRoom_AndTheFirstClaimerCaptures()
    {
        var alice = Member(WorkspaceA);
        var bob = Member(WorkspaceA);
        var booked = (await Service().CreateTranslationRoomAsync(WarpBotCreate(WorkspaceA), alice)).Value!;

        var hosts = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), alice);
        var bobs = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), bob);

        hosts.IsSuccess.Should().BeTrue(hosts.Error);
        hosts.Value!.Created.Should().BeFalse();
        hosts.Value.Room.Id.Should().Be(booked.Id);
        hosts.Value.BridgeRole.Should().Be(BridgeRoomConstants.RoleCapturer);
        bobs.Value!.Room.Id.Should().Be(booked.Id);
        bobs.Value.BridgeRole.Should().Be(BridgeRoomConstants.RoleMember);
        _store.Rooms.Should().ContainSingle();
        _store.Rooms.Single().BridgeCapturerUserId.Should().Be(alice);
    }

    [Fact]
    public async Task Claim_ByAMemberFirst_AdoptsTheWarpBotRoom_AsItsCapturer()
    {
        var alice = Member(WorkspaceA);
        var bob = Member(WorkspaceA);
        var booked = (await Service().CreateTranslationRoomAsync(WarpBotCreate(WorkspaceA), alice)).Value!;

        var bobs = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), bob);

        bobs.IsSuccess.Should().BeTrue(bobs.Error);
        bobs.Value!.Room.Id.Should().Be(booked.Id);
        bobs.Value.BridgeRole.Should().Be(BridgeRoomConstants.RoleCapturer);
        _store.Rooms.Should().ContainSingle();
    }

    [Theory]
    [InlineData(TranslationRoomTypes.Event, null, null)]
    [InlineData(TranslationRoomTypes.ExternalBridge, TranslationRoomConstants.ExternalProviderGoogleMeet, "https://meet.google.com/landing")]
    public async Task OrdinaryCreate_WithoutAParsableMeetLink_StampsNoCode_AndIsNeverDeduplicated(
        string type, string? provider, string? url)
    {
        var alice = Member(WorkspaceA);

        var first = await Service().CreateTranslationRoomAsync(WarpBotCreate(WorkspaceA, url ?? "", type, provider), alice);
        var second = await Service().CreateTranslationRoomAsync(WarpBotCreate(WorkspaceA, url ?? "", type, provider), alice);

        first.IsSuccess.Should().BeTrue(first.Error);
        second.IsSuccess.Should().BeTrue(second.Error);
        second.Value!.Id.Should().NotBe(first.Value!.Id);
        _store.Rooms.Should().HaveCount(2).And.OnlyContain(r => r.ExternalMeetingCode == null);
    }

    // ---- Text-only bridge mode (PO 2026-10-01) -------------------------------------------------

    private TranslationRoomParticipant Row(Guid userId) => _store.Participants.Single(p => p.UserId == userId);

    [Fact]
    public async Task Claim_WithoutAnAudioMode_IsVoice()
    {
        var alice = Member(WorkspaceA);

        var result = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), alice);

        result.Value!.AudioMode.Should().Be(BridgeRoomConstants.AudioModeVoice);
        Row(alice).IsBridgeTextOnly.Should().BeFalse();
        _store.RouteService.Verify(r => r.RefreshDubVoiceAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Claim_AsText_MarksOnlyTheClaimer_AndRepublishesRoutes()
    {
        var alice = Member(WorkspaceA);
        var bob = Member(WorkspaceA);
        var created = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA) with { AudioMode = "TEXT" }, alice);

        var joined = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), bob);

        created.Value!.AudioMode.Should().Be(BridgeRoomConstants.AudioModeText);
        created.Value.Participant!.IsBridgeTextOnly.Should().BeTrue();
        joined.Value!.AudioMode.Should().Be(BridgeRoomConstants.AudioModeVoice);
        Row(alice).IsBridgeTextOnly.Should().BeTrue();
        Row(bob).IsBridgeTextOnly.Should().BeFalse("the mode describes one person's desk, not the room");
        Row(TranslationRoomConstants.ExternalBridgeParticipantUserId).IsBridgeTextOnly.Should().BeFalse();
        _store.RouteService.Verify(r => r.RefreshDubVoiceAsync(created.Value.Room.Id, alice, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Claim_WithAnUnknownAudioMode_IsAValidationError_AndCreatesNothing()
    {
        var alice = Member(WorkspaceA);

        var result = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA) with { AudioMode = "cable" }, alice);

        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _store.Rooms.Should().BeEmpty();
    }

    [Fact]
    public async Task ReClaim_AsVoice_WhileTranslating_KeepsText_ButDoesNotFailTheClaim()
    {
        var alice = Member(WorkspaceA);
        var room = (await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA) with { AudioMode = "text" }, alice)).Value!.Room;
        _store.TranslationActive.Add(room.Id);

        var again = await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA) with { AudioMode = "voice" }, alice);

        again.IsSuccess.Should().BeTrue(again.Error);
        again.Value!.AudioMode.Should().Be(BridgeRoomConstants.AudioModeText);
        Row(alice).IsBridgeTextOnly.Should().BeTrue();
    }

    [Fact]
    public async Task SetAudioMode_VoiceToText_IsAllowedMidMeeting()
    {
        var alice = Member(WorkspaceA);
        var room = (await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), alice)).Value!.Room;
        _store.TranslationActive.Add(room.Id);

        var result = await Service().SetBridgeAudioModeAsync(room.Id, alice, "text");

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Mode.Should().Be(BridgeRoomConstants.AudioModeText);
        result.Value.TranslationActive.Should().BeTrue();
        Row(alice).IsBridgeTextOnly.Should().BeTrue();
        _store.RouteService.Verify(r => r.RefreshDubVoiceAsync(room.Id, alice, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetAudioMode_TextToVoice_IsLockedWhileTranslating_AndFreeOtherwise()
    {
        var alice = Member(WorkspaceA);
        var room = (await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA) with { AudioMode = "text" }, alice)).Value!.Room;
        _store.TranslationActive.Add(room.Id);

        var locked = await Service().SetBridgeAudioModeAsync(room.Id, alice, "voice");

        locked.IsSuccess.Should().BeFalse();
        locked.ErrorCode.Should().Be(BridgeRoomConstants.ErrorCodeAudioModeLocked);
        Row(alice).IsBridgeTextOnly.Should().BeTrue();

        // Stop translation: the choice is free again, as it was before Start.
        _store.TranslationActive.Remove(room.Id);
        var unlocked = await Service().SetBridgeAudioModeAsync(room.Id, alice, "voice");

        unlocked.IsSuccess.Should().BeTrue(unlocked.Error);
        unlocked.Value!.Mode.Should().Be(BridgeRoomConstants.AudioModeVoice);
        Row(alice).IsBridgeTextOnly.Should().BeFalse();
    }

    [Fact]
    public async Task SetAudioMode_ToTheCurrentMode_WritesAndRepublishesNothing()
    {
        var alice = Member(WorkspaceA);
        var room = (await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), alice)).Value!.Room;

        var result = await Service().SetBridgeAudioModeAsync(room.Id, alice, "voice");

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Mode.Should().Be(BridgeRoomConstants.AudioModeVoice);
        _store.RouteService.Verify(r => r.RefreshDubVoiceAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetAudioMode_Refusals()
    {
        var alice = Member(WorkspaceA);
        var room = (await Service().ClaimBridgeRoomAsync(Claim(WorkspaceA), alice)).Value!.Room;

        (await Service().SetBridgeAudioModeAsync(room.Id, alice, "loud")).ErrorCode.Should().Be(ErrorCodes.ValidationError);
        (await Service().SetBridgeAudioModeAsync(room.Id, Member(WorkspaceA), "text")).ErrorCode.Should().Be(ErrorCodes.Forbidden);
        (await Service().SetBridgeAudioModeAsync(room.Id, TranslationRoomConstants.ExternalBridgeParticipantUserId, "text"))
            .ErrorCode.Should().Be(ErrorCodes.Forbidden);
        (await Service().SetBridgeAudioModeAsync(Guid.NewGuid(), alice, "text")).ErrorCode.Should().Be(ErrorCodes.NotFound);

        _store.Rooms.Single().Status = "ENDED";
        (await Service().SetBridgeAudioModeAsync(room.Id, alice, "text")).ErrorCode.Should().Be(ErrorCodes.InvalidState);
    }

    [Fact]
    public void RouteMapper_MarksOnlyATextOnlySpeakersRouteToTheStandIn()
    {
        var roomId = Guid.NewGuid();
        var host = new TranslationRoomParticipant { Id = Guid.NewGuid(), TranslationRoomId = roomId, UserId = Guid.NewGuid(), IsBridgeTextOnly = true };
        var member = new TranslationRoomParticipant { Id = Guid.NewGuid(), TranslationRoomId = roomId, UserId = Guid.NewGuid() };
        var standIn = new TranslationRoomParticipant { Id = Guid.NewGuid(), TranslationRoomId = roomId, UserId = TranslationRoomConstants.ExternalBridgeParticipantUserId };

        TranslationRoomAudioRoute Route(TranslationRoomParticipant source, TranslationRoomParticipant target) => new()
        {
            Id = Guid.NewGuid(), TranslationRoomId = roomId,
            SourceParticipantId = source.Id, TargetParticipantId = target.Id,
            SourceParticipant = source, TargetParticipant = target,
            SourceLanguage = "vi", TargetLanguage = "en", Status = "BROADCASTING",
        };

        TranslationRoomAudioRouteMapper.ToDto(Route(host, standIn)).TextOnly.Should().BeTrue("host → Meet: the dub nobody can play");
        TranslationRoomAudioRouteMapper.ToDto(Route(host, member)).TextOnly.Should().BeFalse("another WarpTalk listener still hears the host's dub");
        TranslationRoomAudioRouteMapper.ToDto(Route(standIn, host)).TextOnly.Should().BeFalse("inbound is never text-only");
        TranslationRoomAudioRouteMapper.ToDto(Route(member, standIn)).TextOnly.Should().BeFalse("the member has a cable");
    }

    [Theory]
    [InlineData("abc-defg-hij", "abc-defg-hij")]
    [InlineData(" ABC-DEFG-HIJ ", "abc-defg-hij")]
    [InlineData("https://meet.google.com/abc-defg-hij", "abc-defg-hij")]
    [InlineData("meet.google.com/abc-defg-hij?authuser=1", "abc-defg-hij")]
    [InlineData("https://meet.google.com/abc-defg-hij/", "abc-defg-hij")]
    public void MeetCode_Normalizes(string input, string expected)
    {
        GoogleMeetCode.TryNormalize(input, out var code).Should().BeTrue();
        code.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abcdefghij")]
    [InlineData("https://meet.google.com/landing")]
    [InlineData("https://meet.google.com/abc-defg-hijk-lmn")]
    public void MeetCode_RejectsWhatIsNotACode(string? input)
    {
        GoogleMeetCode.TryNormalize(input, out _).Should().BeFalse();
    }

    /// <summary>
    /// The database, as far as claim can tell: tracked-then-committed rows, and the open-bridge
    /// unique index.
    /// </summary>
    private sealed class FakeBridgeStore
    {
        public List<TranslationRoom> Rooms { get; } = new();
        public List<TranslationRoomParticipant> Participants { get; } = new();
        public List<object> Pending { get; } = new();
        public HashSet<(Guid WorkspaceId, Guid UserId)> Members { get; } = new();
        public HashSet<Guid> DenyCreationFor { get; } = new();
        /// <summary>Rooms with an ACTIVE translation session (Start pressed, not stopped).</summary>
        public HashSet<Guid> TranslationActive { get; } = new();
        public Mock<ITranslationRoomAudioRouteService> RouteService { get; } = new();
        public int StaleLookupsRemaining { get; set; }
        public int UniqueViolationsRaised { get; private set; }

        private readonly object _gate = new();

        private static bool IsOpenBridge(TranslationRoom room) =>
            room.TranslationRoomType == TranslationRoomTypes.ExternalBridge
            && !BridgeRoomConstants.ClosedStatuses.Contains(room.Status)
            && room.DeletedAt == null;

        public RoomService BuildService(Func<DateTime> clock)
        {
            var uow = new Mock<IUnitOfWork>();
            var rooms = new Mock<ITranslationRoomRepository>();
            var participants = new Mock<ITranslationRoomParticipantRepository>();

            uow.Setup(u => u.TranslationRoomRepository).Returns(rooms.Object);
            uow.Setup(u => u.TranslationRoomParticipantRepository).Returns(participants.Object);
            uow.Setup(u => u.TranslationRoomAudioRouteRepository).Returns(new Mock<ITranslationRoomAudioRouteRepository>().Object);
            var sessions = new Mock<ITranslationRoomSessionRepository>();
            sessions.Setup(s => s.GetActiveSessionByRoomIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid roomId, CancellationToken _) =>
                    Locked(() => TranslationActive.Contains(roomId) ? new TranslationRoomSession { TranslationRoomId = roomId } : null));
            uow.Setup(u => u.TranslationRoomSessionRepository).Returns(sessions.Object);
            uow.Setup(u => u.TranslationRoomInvitationRepository).Returns(new Mock<ITranslationRoomInvitationRepository>().Object);
            uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(() => Task.FromResult(Commit()));

            rooms.Setup(r => r.AddAsync(It.IsAny<TranslationRoom>(), It.IsAny<CancellationToken>()))
                .Callback<TranslationRoom, CancellationToken>((room, _) => { lock (_gate) Pending.Add(room); })
                .Returns(Task.CompletedTask);
            rooms.Setup(r => r.Remove(It.IsAny<TranslationRoom>()))
                .Callback<TranslationRoom>(room => { lock (_gate) Pending.Remove(room); });
            rooms.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => Locked(() => Rooms.FirstOrDefault(r => r.Id == id)));
            rooms.Setup(r => r.GetByCodeAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string code, IEnumerable<string>? _, CancellationToken _) =>
                    Locked(() => Rooms.Where(r => r.TranslationRoomCode == code).OrderBy(r => IsOpenBridge(r) ? 0 : 1).FirstOrDefault()));
            rooms.Setup(r => r.ExistsByCodeAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            rooms.Setup(r => r.GetOpenBridgeRoomByMeetCodeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid ws, string code, CancellationToken _) => Locked(() =>
                {
                    if (StaleLookupsRemaining > 0)
                    {
                        StaleLookupsRemaining--;
                        return null;
                    }
                    return Rooms.FirstOrDefault(r => r.WorkspaceId == ws && r.ExternalMeetingCode == code && IsOpenBridge(r));
                }));
            rooms.Setup(r => r.TryAcquireBridgeCapturerAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, Guid user, DateTime now, DateTime staleBefore, CancellationToken _) => Locked(() =>
                {
                    var room = Rooms.Single(r => r.Id == id);
                    var free = room.BridgeCapturerUserId == null
                        || room.BridgeCapturerUserId == user
                        || room.BridgeCapturerHeartbeatAt == null
                        || room.BridgeCapturerHeartbeatAt < staleBefore;
                    if (!free) return false;
                    room.BridgeCapturerUserId = user;
                    room.BridgeCapturerHeartbeatAt = now;
                    return true;
                }));
            rooms.Setup(r => r.TryRenewBridgeCapturerAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, Guid user, DateTime now, CancellationToken _) => Locked(() =>
                {
                    var room = Rooms.Single(r => r.Id == id);
                    if (room.BridgeCapturerUserId != user) return false;
                    room.BridgeCapturerHeartbeatAt = now;
                    return true;
                }));

            participants.Setup(p => p.AddAsync(It.IsAny<TranslationRoomParticipant>(), It.IsAny<CancellationToken>()))
                .Callback<TranslationRoomParticipant, CancellationToken>((row, _) => { lock (_gate) Pending.Add(row); })
                .Returns(Task.CompletedTask);
            participants.Setup(p => p.Remove(It.IsAny<TranslationRoomParticipant>()))
                .Callback<TranslationRoomParticipant>(row => { lock (_gate) Pending.Remove(row); });
            participants.Setup(p => p.GetByRoomAndUserAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid roomId, Guid userId, CancellationToken _) =>
                    Locked(() => Participants.FirstOrDefault(p => p.TranslationRoomId == roomId && p.UserId == userId)));
            participants.Setup(p => p.CountSeatHoldingParticipantsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid roomId, CancellationToken _) =>
                    Locked(() => Participants.Count(p => p.TranslationRoomId == roomId && TranslationRoomParticipantStatuses.HoldsSeat(p.Status))));
            participants.Setup(p => p.CountPeopleInRoomsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => Locked(() =>
                    Participants.Where(p => ids.Contains(p.TranslationRoomId) && RoomPresence.IsPersonInRoom(p))
                        .GroupBy(p => p.TranslationRoomId)
                        .ToDictionary(g => g.Key, g => g.Count())));
            participants.Setup(p => p.CountEverJoinedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

            var languagePolicy = new Mock<ILanguagePolicy>();
            languagePolicy.Setup(p => p.IsSupportedAsync(It.IsAny<string>())).ReturnsAsync(true);
            languagePolicy.Setup(p => p.ValidateParticipantLanguagesAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<TranslationRoom>()))
                .ReturnsAsync((string?)null);

            var meetingPolicy = new Mock<IWorkspaceMeetingPolicy>();
            meetingPolicy.Setup(p => p.ValidateMeetingCreationAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, Guid host, IEnumerable<string> _, string? _, CancellationToken _) =>
                    DenyCreationFor.Contains(host)
                        ? Result.Failure("You do not have permission to create meetings in this workspace.", ErrorCodes.Forbidden)
                        : Result.Success());
            meetingPolicy.Setup(p => p.EnsureWorkspaceCanHostMeetingsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success());

            var memberDirectory = new Mock<IWorkspaceMemberDirectory>();
            memberDirectory.Setup(d => d.IsMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid ws, Guid user, CancellationToken _) => Locked(() => Members.Contains((ws, user))));

            var userSettings = new Mock<IUserSettingsDirectory>();
            userSettings.Setup(d => d.GetDefaultsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UserLanguageDefaults("vi", "en"));
            userSettings.Setup(d => d.GetDisplayNameAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid user, CancellationToken _) => $"User {user.ToString()[..4]}");

            RouteService.Setup(r => r.RefreshDubVoiceAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success(new List<TranslationRoomAudioRouteDto>()));

            return new RoomService(
                uow.Object,
                languagePolicy.Object,
                new Mock<IAudioRouteEventProcessor>().Object,
                RouteService.Object,
                userSettings.Object,
                meetingPolicy.Object,
                memberDirectory.Object,
                new Mock<WarpTalk.Shared.Interfaces.IEmailService>().Object,
                new Mock<Microsoft.Extensions.Logging.ILogger<RoomService>>().Object,
                utcNow: clock);
        }

        private T Locked<T>(Func<T> read)
        {
            lock (_gate) return read();
        }

        private int Commit()
        {
            lock (_gate)
            {
                foreach (var room in Pending.OfType<TranslationRoom>())
                {
                    var duplicate = room.ExternalMeetingCode != null && IsOpenBridge(room) && Rooms.Any(r =>
                        r.WorkspaceId == room.WorkspaceId && r.ExternalMeetingCode == room.ExternalMeetingCode && IsOpenBridge(r));
                    if (duplicate)
                    {
                        UniqueViolationsRaised++;
                        throw new DbUpdateException(
                            "duplicate key value violates unique constraint \"translation_rooms_open_bridge_meet_code_key\"",
                            new UniqueViolation());
                    }
                }

                var written = Pending.Count;
                foreach (var row in Pending)
                {
                    switch (row)
                    {
                        case TranslationRoom room: Rooms.Add(room); break;
                        case TranslationRoomParticipant participant: Participants.Add(participant); break;
                    }
                }
                Pending.Clear();
                return written;
            }
        }
    }

    private sealed class UniqueViolation : DbException
    {
        public UniqueViolation() : base("23505: duplicate key value violates unique constraint") { }
        public override string SqlState => "23505";
    }
}

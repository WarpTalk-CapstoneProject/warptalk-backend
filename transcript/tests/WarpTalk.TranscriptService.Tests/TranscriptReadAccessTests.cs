using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.Shared.Protos;
using WarpTalk.TranscriptService.Application.Authorization;
using WarpTalk.TranscriptService.Application.Services;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranscriptService.Tests;

/// <summary>
/// Who may read a transcript.
/// </summary>
/// <remarks>
/// <para>
/// <c>TranscriptQueryService.CanAccessTranscriptAsync</c> returned an unconditional <c>true</c>:
/// the participant clause was commented out under a "WT-65: Loosen permissions" note and replaced
/// with <c>return true</c>, while the host check above it stayed, so the method read like a working
/// gate. The effect was cross-tenant — a user in workspace A could pass any transcript GUID from
/// workspace B to <c>GET /api/v1/transcripts/{id}</c> and receive a 200 with the whole transcript,
/// and the same for <c>/segments</c>, <c>/translations</c> and <c>by-room/{roomId}</c>.
/// </para>
/// <para>
/// These tests drive the real service against a stand-in for the generated gRPC client (the same
/// subclass-the-client approach <c>ReminderNotificationWorkerTests</c> uses), so what is exercised
/// is the production decision path, including the two round trips it makes and the order it makes
/// them in — not a re-implementation of the rule.
/// </para>
/// </remarks>
public class TranscriptReadAccessTests
{
    private static readonly Guid RoomId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Stranger_IsRefused_TheTranscript()
    {
        var stranger = Guid.NewGuid();
        var (service, transcript, _) = CreateQueryService(host: Guid.NewGuid(), participants: new[] { Guid.NewGuid() });

        var result = await service.GetTranscriptAsync(transcript.Id, stranger);

        Assert.False(result.IsSuccess);
        Assert.Equal("FORBIDDEN", result.ErrorCode);
    }

    [Fact]
    public async Task Stranger_IsRefused_ByRoomLookupSegmentsAndTranslations()
    {
        var stranger = Guid.NewGuid();
        var (service, transcript, _) = CreateQueryService(host: Guid.NewGuid(), participants: Array.Empty<Guid>());

        Assert.Equal("FORBIDDEN", (await service.GetTranscriptByTranslationRoomAsync(RoomId, stranger)).ErrorCode);
        Assert.Equal("FORBIDDEN", (await service.GetSegmentsAsync(transcript.Id, stranger, 0, 50)).ErrorCode);
        Assert.Equal("FORBIDDEN", (await service.GetTranslationsAsync(transcript.Id, stranger, 0, 50)).ErrorCode);
    }

    [Fact]
    public async Task Host_StillGetsTheTranscript()
    {
        var host = Guid.NewGuid();
        var (service, transcript, _) = CreateQueryService(host, participants: Array.Empty<Guid>());

        var result = await service.GetTranscriptAsync(transcript.Id, host);

        Assert.True(result.IsSuccess);
        Assert.Equal(transcript.Id, result.Value!.Id);
    }

    [Fact]
    public async Task Participant_IsRefused_WhileTheHostHasNotSharedTheRecord()
    {
        // The rule item 5 adds. The transcript is one of the three things the host's Publish
        // control claims to cover, and it was the one that never consulted the setting: a
        // participant could read the whole transcript of a meeting the host had deliberately kept
        // private, while being refused the same text as a download.
        var participant = Guid.NewGuid();
        var (service, transcript, _) = CreateQueryService(host: Guid.NewGuid(), participants: new[] { participant });

        var result = await service.GetTranscriptAsync(transcript.Id, participant);

        Assert.False(result.IsSuccess);
        Assert.Equal("FORBIDDEN", result.ErrorCode);
    }

    [Fact]
    public async Task Host_ReadsTheirOwnUnsharedTranscript()
    {
        // The host is never gated by the switch they own — otherwise publishing would be the only
        // way to check what you are about to publish.
        var host = Guid.NewGuid();
        var (service, transcript, _) = CreateQueryService(host, participants: Array.Empty<Guid>());

        Assert.True((await service.GetTranscriptAsync(transcript.Id, host)).IsSuccess);
    }

    [Fact]
    public async Task AnUnknownAccessLevel_ReadsAsHostOnly()
    {
        // Fail closed on a value this build does not know, the same direction
        // ArtifactAccessHelper.ReadArtifactAccessLevel fails in for an unparseable settings blob.
        var participant = Guid.NewGuid();
        var (service, transcript, client) = CreateQueryService(host: Guid.NewGuid(), participants: new[] { participant });
        client.ArtifactAccess = "EVERYONE_ON_THE_INTERNET";

        Assert.Equal("FORBIDDEN", (await service.GetTranscriptAsync(transcript.Id, participant)).ErrorCode);
    }

    [Fact]
    public async Task TheNewHost_ReadsTheTranscript_AfterATransfer()
    {
        // Item 2. `HostId` is the booker and does not move on a transfer; every host gate inside
        // TranslationRoomService asks the EFFECTIVE host. Comparing against the booker refused the
        // person the rest of the product calls the host.
        var booker = Guid.NewGuid();
        var newHost = Guid.NewGuid();
        var (service, transcript, client) = CreateQueryService(booker, participants: Array.Empty<Guid>());
        client.EffectiveHostId = newHost.ToString();

        Assert.True((await service.GetTranscriptAsync(transcript.Id, newHost)).IsSuccess);
    }

    [Fact]
    public async Task Participant_GetsTheTranscript_OnceTheRecordIsShared()
    {
        var participant = Guid.NewGuid();
        var (service, transcript, client) = CreateQueryService(host: Guid.NewGuid(), participants: new[] { participant });
        client.ArtifactAccess = "ALL_PARTICIPANTS";

        var result = await service.GetTranscriptAsync(transcript.Id, participant);

        Assert.True(result.IsSuccess);
        Assert.Equal(transcript.Id, result.Value!.Id);
    }

    [Fact]
    public async Task Host_IsAnsweredWithoutAskingForTheRoster()
    {
        var host = Guid.NewGuid();
        var (service, transcript, client) = CreateQueryService(host, participants: Array.Empty<Guid>());

        Assert.True((await service.GetTranscriptAsync(transcript.Id, host)).IsSuccess);
        Assert.Equal(0, client.ParticipantLookups);
    }

    /// <summary>
    /// A caller invited by email who never joined is NOT a participant, so they are refused — and
    /// that is deliberate, not an oversight. RoomReadAccess does grant such a caller room-level
    /// read on the translation-room side, but a standing invitation is what puts a room on your
    /// list, not consent to read what was said in a meeting you never attended. The transcript
    /// service also cannot ask the question: translation_room.proto exposes the room and its
    /// participants and has no invitation-aware RPC at all.
    /// </summary>
    [Fact]
    public async Task InvitedByEmailButNeverJoined_IsRefused()
    {
        var invitee = Guid.NewGuid();
        var (service, transcript, _) = CreateQueryService(host: Guid.NewGuid(), participants: new[] { Guid.NewGuid() });

        var result = await service.GetTranscriptAsync(transcript.Id, invitee);

        Assert.False(result.IsSuccess);
        Assert.Equal("FORBIDDEN", result.ErrorCode);
    }

    [Fact]
    public async Task RoomThatNoLongerExists_IsRefused_NotThrown()
    {
        var access = new TranscriptReadAccess(new FakeRoomClient(Guid.NewGuid(), Array.Empty<Guid>()) { RoomMissing = true });

        Assert.False(await access.CanReadRoomTranscriptAsync(RoomId, Guid.NewGuid()));
    }

    /// <summary>
    /// The consolidation itself. Correction and export each carried their own byte-identical copy
    /// of this method; one of the three then drifted to <c>return true</c> and nothing noticed
    /// because the other two still enforced it. All three now resolve through one predicate, so
    /// there is a single place left that could ever drift.
    /// </summary>
    [Fact]
    public void AllThreeTranscriptServicesDependOnTheOneSharedPredicate()
    {
        foreach (var type in new[]
                 {
                     typeof(TranscriptQueryService),
                     typeof(TranscriptCorrectionService),
                     typeof(TranscriptExportService)
                 })
        {
            var takesTheSharedPredicate = false;
            foreach (var ctor in type.GetConstructors())
            {
                foreach (var parameter in ctor.GetParameters())
                {
                    if (parameter.ParameterType == typeof(ITranscriptReadAccess))
                        takesTheSharedPredicate = true;
                }
            }

            Assert.True(takesTheSharedPredicate, $"{type.Name} must consume ITranscriptReadAccess");
        }
    }

    /// <summary>
    /// The sharing policy is about a FINISHED meeting's record. While the meeting is still running
    /// this same endpoint is what catches a late joiner up on the part they missed — and they are
    /// in the room, listening to the captions, as it refuses them the transcript of what they are
    /// hearing. Withholding a record from somebody currently being told its contents protects
    /// nobody, and every room left on the HOST_ONLY default would do it.
    /// </summary>
    [Fact]
    public async Task LiveMeeting_LetsAParticipantCatchUp_WhateverTheSharingPolicySays()
    {
        var participant = Guid.NewGuid();
        var (service, transcript, client) = CreateQueryService(
            host: Guid.NewGuid(), participants: new[] { participant });
        client.RoomStatus = "IN_PROGRESS";
        client.ArtifactAccess = "HOST_ONLY";

        var result = await service.GetTranscriptAsync(transcript.Id, participant);

        Assert.True(result.IsSuccess);
    }

    /// <summary>
    /// And a live room is not a way in for somebody who was never there.
    /// </summary>
    [Fact]
    public async Task LiveMeeting_StillRefusesAStranger()
    {
        var (service, transcript, client) = CreateQueryService(
            host: Guid.NewGuid(), participants: new[] { Guid.NewGuid() });
        client.RoomStatus = "IN_PROGRESS";
        client.ArtifactAccess = "ALL_PARTICIPANTS";

        var result = await service.GetTranscriptAsync(transcript.Id, Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal("FORBIDDEN", result.ErrorCode);
    }

    private static (TranscriptQueryService Service, Transcript Transcript, FakeRoomClient Client) CreateQueryService(
        Guid host, IReadOnlyCollection<Guid> participants)
    {
        var transcript = new Transcript
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = RoomId,
            WorkspaceId = Guid.NewGuid(),
            Status = "COMPLETED",
            SourceLanguage = "en-US",
            IsCurrent = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var transcripts = Substitute.For<ITranscriptRepository>();
        transcripts.GetByIdAsync(transcript.Id, Arg.Any<CancellationToken>()).Returns(transcript);
        transcripts.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Transcript, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<Transcript> { transcript });

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.Transcripts.Returns(transcripts);

        var client = new FakeRoomClient(host, participants);
        var service = new TranscriptQueryService(
            unitOfWork,
            new TranscriptReadAccess(client),
            NullLogger<TranscriptQueryService>.Instance);

        return (service, transcript, client);
    }

    /// <summary>
    /// A stand-in for the generated client. Its async methods are virtual and it has a protected
    /// parameterless constructor, so overriding the two calls the predicate makes exercises the
    /// real call path — including that the host answer short-circuits the roster lookup.
    /// </summary>
    private sealed class FakeRoomClient : TranslationRoomService.TranslationRoomServiceClient
    {
        private readonly Guid _hostId;
        private readonly IReadOnlyCollection<Guid> _participants;

        public FakeRoomClient(Guid hostId, IReadOnlyCollection<Guid> participants)
        {
            _hostId = hostId;
            _participants = participants;
        }

        /// <summary>
        /// The room's visibility switch. Defaults to EMPTY on purpose — that is what an older
        /// TranslationRoomService sends, and the gate must read it as HOST_ONLY.
        /// </summary>
        public string ArtifactAccess { get; set; } = string.Empty;

        /// <summary>Empty unless a test hands the room over, matching the wire default.</summary>
        public string EffectiveHostId { get; set; } = string.Empty;

        /// <summary>
        /// The room's lifecycle status. The sharing policy only governs a meeting that has ended.
        ///
        /// Named RoomStatus, not Status: a member called Status on this class would shadow
        /// Grpc.Core.Status for the whole type, and Call() below needs the gRPC one.
        /// </summary>
        public string RoomStatus { get; set; } = "ENDED";

        public bool RoomMissing { get; set; }
        public int ParticipantLookups { get; private set; }

        public override AsyncUnaryCall<GetTranslationRoomResponse> GetTranslationRoomByIdAsync(
            GetTranslationRoomRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            if (RoomMissing)
                throw new RpcException(new Status(StatusCode.NotFound, "room not found"));

            return Call(new GetTranslationRoomResponse
            {
                Id = request.Id,
                HostId = _hostId.ToString(),
                EffectiveHostId = EffectiveHostId,
                ArtifactAccess = ArtifactAccess,
                Title = "Room",
                Status = RoomStatus
            });
        }

        public override AsyncUnaryCall<GetParticipantsByRoomIdResponse> GetParticipantsByRoomIdAsync(
            GetParticipantsByRoomIdRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            ParticipantLookups++;

            var response = new GetParticipantsByRoomIdResponse();
            foreach (var id in _participants)
            {
                response.Participants.Add(new Participant
                {
                    Id = id.ToString(),
                    DisplayName = "Participant",
                    Role = "PARTICIPANT",
                    IsActive = true
                });
            }

            return Call(response);
        }

        private static AsyncUnaryCall<T> Call<T>(T value) => new(
            Task.FromResult(value),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
    }
}

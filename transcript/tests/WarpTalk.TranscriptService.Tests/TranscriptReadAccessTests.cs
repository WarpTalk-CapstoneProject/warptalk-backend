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
    public async Task Participant_StillGetsTheTranscript()
    {
        var participant = Guid.NewGuid();
        var (service, transcript, _) = CreateQueryService(host: Guid.NewGuid(), participants: new[] { participant });

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
                Title = "Room",
                Status = "ENDED"
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

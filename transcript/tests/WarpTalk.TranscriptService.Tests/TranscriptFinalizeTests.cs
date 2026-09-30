using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WarpTalk.Shared.Protos;
using WarpTalk.TranscriptService.Application.Authorization;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.TranscriptService.Application.Services;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranscriptService.Tests;

/// <summary>
/// Function 51 — Finalize Meeting Transcript: <see cref="TranscriptCorrectionService.FinalizeTranscriptAsync"/>.
/// The host check (effective host from the room lookup) runs before any status check.
/// </summary>
public class TranscriptFinalizeTests
{
    private static readonly Guid TranscriptId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RoomId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid HostId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid OtherUserId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ITranscriptRepository _transcripts = Substitute.For<ITranscriptRepository>();
    private readonly ILogger<TranscriptCorrectionService> _logger = Substitute.For<ILogger<TranscriptCorrectionService>>();
    private readonly FakeRoomClient _roomClient = new();
    private readonly TranscriptCorrectionService _service;
    private readonly Transcript _transcript;

    public TranscriptFinalizeTests()
    {
        _unitOfWork.Transcripts.Returns(_transcripts);

        _transcript = new Transcript
        {
            Id = TranscriptId,
            TranslationRoomId = RoomId,
            WorkspaceId = Guid.NewGuid(),
            Status = "ACTIVE",
            IsActive = true,
            SourceLanguage = "vi",
        };
        _transcripts.GetByIdAsync(TranscriptId, Arg.Any<CancellationToken>()).Returns(_transcript);

        _service = new TranscriptCorrectionService(
            _unitOfWork,
            Substitute.For<ITranscriptReadAccess>(),
            _roomClient,
            Substitute.For<ITranscriptRoomLanguagePolicy>(),
            Substitute.For<ITranscriptTranslationBackfillService>(),
            _logger);
    }

    // UTCID01
    [Fact]
    public async Task Finalize_ActiveTranscript_ByHost_MarksFinalizedAndSaves()
    {
        var before = DateTime.UtcNow;

        var result = await _service.FinalizeTranscriptAsync(TranscriptId, HostId);

        Assert.True(result.IsSuccess);
        Assert.Equal("FINALIZED", _transcript.Status);
        Assert.False(_transcript.IsActive);
        Assert.NotNull(_transcript.FinalizedAt);
        Assert.True(_transcript.FinalizedAt >= before);
        Assert.Equal(_transcript.FinalizedAt, _transcript.UpdatedAt);
        Assert.Equal(HostId, _transcript.UpdatedBy);
        Assert.Equal(RoomId.ToString(), _roomClient.LastRequestedRoomId);
        _transcripts.Received(1).Update(_transcript);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // UTCID02
    [Fact]
    public async Task Finalize_MissingTranscript_ReturnsNotFound()
    {
        var missingId = Guid.NewGuid();
        _transcripts.GetByIdAsync(missingId, Arg.Any<CancellationToken>()).Returns((Transcript?)null);

        var result = await _service.FinalizeTranscriptAsync(missingId, HostId);

        Assert.False(result.IsSuccess);
        Assert.Equal("NOT_FOUND", result.ErrorCode);
        Assert.Null(_roomClient.LastRequestedRoomId);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // UTCID03
    [Fact]
    public async Task Finalize_DeletedTranscript_ReturnsNotFound()
    {
        _transcript.DeletedAt = DateTime.UtcNow.AddDays(-1);

        var result = await _service.FinalizeTranscriptAsync(TranscriptId, HostId);

        Assert.False(result.IsSuccess);
        Assert.Equal("NOT_FOUND", result.ErrorCode);
        Assert.Null(_roomClient.LastRequestedRoomId);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // UTCID04
    [Fact]
    public async Task Finalize_ByNonHost_ReturnsUnauthorized_AndChangesNothing()
    {
        var result = await _service.FinalizeTranscriptAsync(TranscriptId, OtherUserId);

        Assert.False(result.IsSuccess);
        Assert.Equal("UNAUTHORIZED", result.ErrorCode);
        Assert.Equal("ACTIVE", _transcript.Status);
        Assert.True(_transcript.IsActive);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // UTCID05
    [Fact]
    public async Task Finalize_WhenRoomNotFoundInGrpc_ReturnsNotFound()
    {
        _roomClient.Error = new RpcException(new Status(StatusCode.NotFound, "room not found"));

        var result = await _service.FinalizeTranscriptAsync(TranscriptId, HostId);

        Assert.False(result.IsSuccess);
        Assert.Equal("NOT_FOUND", result.ErrorCode);
        Assert.Equal("ACTIVE", _transcript.Status);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // UTCID06
    [Fact]
    public async Task Finalize_ArchivedTranscript_ByHost_ReturnsBadRequest()
    {
        _transcript.Status = "ARCHIVED";

        var result = await _service.FinalizeTranscriptAsync(TranscriptId, HostId);

        Assert.False(result.IsSuccess);
        Assert.Equal("BAD_REQUEST", result.ErrorCode);
        Assert.Equal("ARCHIVED", _transcript.Status);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // UTCID07
    [Fact]
    public async Task Finalize_AlreadyFinalizedTranscript_ByHost_IsIdempotentSuccess()
    {
        var finalizedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _transcript.Status = "FINALIZED";
        _transcript.IsActive = false;
        _transcript.FinalizedAt = finalizedAt;

        var result = await _service.FinalizeTranscriptAsync(TranscriptId, HostId);

        Assert.True(result.IsSuccess);
        Assert.Equal(finalizedAt, _transcript.FinalizedAt);
        _transcripts.DidNotReceiveWithAnyArgs().Update(default!);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // UTCID08
    [Fact]
    public async Task Finalize_WhenSaveChangesThrows_ReturnsInternalError_AndLogsError()
    {
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        var result = await _service.FinalizeTranscriptAsync(TranscriptId, HostId);

        Assert.False(result.IsSuccess);
        Assert.Equal("INTERNAL_ERROR", result.ErrorCode);
        Assert.Contains(_logger.ReceivedCalls(), call =>
            call.GetMethodInfo().Name == nameof(ILogger.Log)
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Error
            && call.GetArguments()[3] is InvalidOperationException);
    }

    /// <summary>
    /// A room whose effective host is <see cref="HostId"/>. The booker (<c>HostId</c> on the
    /// response) is deliberately someone else, so only the effective-host comparison can pass.
    /// </summary>
    private sealed class FakeRoomClient : TranslationRoomService.TranslationRoomServiceClient
    {
        public RpcException? Error { get; set; }

        public string? LastRequestedRoomId { get; private set; }

        public override AsyncUnaryCall<GetTranslationRoomResponse> GetTranslationRoomByIdAsync(
            GetTranslationRoomRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            LastRequestedRoomId = request.Id;

            var response = Error is null
                ? Task.FromResult(new GetTranslationRoomResponse
                {
                    Id = request.Id,
                    HostId = OtherUserId.ToString(),
                    EffectiveHostId = HostId.ToString(),
                    Status = "ENDED",
                })
                : Task.FromException<GetTranslationRoomResponse>(Error);

            return new AsyncUnaryCall<GetTranslationRoomResponse>(
                response,
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }
    }
}

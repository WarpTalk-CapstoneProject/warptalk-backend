using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using WarpTalk.Shared.Protos;
using WarpTalk.TranscriptService.Application.Authorization;
using Xunit;

namespace WarpTalk.TranscriptService.Tests;

/// <summary>
/// WT-704: <see cref="TranscriptRoomLanguagePolicy"/> reads TranslationRoomService's L2 ∩ L1 answer
/// and, when there is none, fails closed to the room's own declared set — never open.
/// </summary>
public class TranscriptRoomLanguagePolicyTests
{
    private static readonly Guid RoomId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BookerId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid EffectiveHostId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly FakeRoomClient _roomClient = new();
    private readonly TranscriptRoomLanguagePolicy _policy;

    public TranscriptRoomLanguagePolicyTests()
    {
        _policy = new TranscriptRoomLanguagePolicy(_roomClient, NullLogger<TranscriptRoomLanguagePolicy>.Instance);
    }

    [Fact]
    public async Task Resolved_UsesTheGeneratableList_NotTheRoomsDeclaredSet()
    {
        _roomClient.Response = Room(source: "vi", targets: ["en", "ja"], generatable: ["vi", "en"], resolved: true);

        var result = await _policy.GetAsync(RoomId);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(["vi", "en"], result.Value!.AllowedLanguages);
        Assert.Equal(EffectiveHostId, result.Value.EffectiveHostId);
    }

    [Fact]
    public async Task Request_OptsInToArtifactLanguages()
    {
        _roomClient.Response = Room(source: "vi", targets: ["en"], generatable: [], resolved: true);

        await _policy.GetAsync(RoomId);

        Assert.NotNull(_roomClient.LastRequest);
        Assert.Equal(RoomId.ToString(), _roomClient.LastRequest!.Id);
        Assert.True(_roomClient.LastRequest.IncludeArtifactLanguages);
    }

    /// <summary>
    /// An empty list WITH resolved=true is an answer — nothing may be generated — and must not be
    /// mistaken for "no answer" and widened back to the room's set.
    /// </summary>
    [Fact]
    public async Task ResolvedEmpty_AllowsNothing()
    {
        _roomClient.Response = Room(source: "vi", targets: ["en"], generatable: [], resolved: true);

        var result = await _policy.GetAsync(RoomId);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(result.Value!.AllowedLanguages);
    }

    [Fact]
    public async Task Unresolved_FallsBackToSourceAndTargets()
    {
        _roomClient.Response = Room(source: "vi", targets: ["en", "vi"], generatable: [], resolved: false);

        var result = await _policy.GetAsync(RoomId);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(["vi", "en"], result.Value!.AllowedLanguages);
    }

    [Fact]
    public async Task LocaleTags_AreNormalizedToBareCodes()
    {
        _roomClient.Response = Room(source: "vi-VN", targets: ["EN_us"], generatable: ["vi-VN", "en-US", "vi"], resolved: true);

        var result = await _policy.GetAsync(RoomId);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(["vi", "en"], result.Value!.AllowedLanguages);
    }

    /// <summary>An older server leaves effective_host_id empty; the booker is the host then.</summary>
    [Fact]
    public async Task EmptyEffectiveHost_FallsBackToTheBooker()
    {
        _roomClient.Response = Room(source: "vi", targets: [], generatable: [], resolved: false);
        _roomClient.Response.EffectiveHostId = string.Empty;

        var result = await _policy.GetAsync(RoomId);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(BookerId, result.Value!.EffectiveHostId);
    }

    [Fact]
    public async Task MissingRoom_IsNotFound()
    {
        _roomClient.Error = new RpcException(new Status(StatusCode.NotFound, "room not found"));

        var result = await _policy.GetAsync(RoomId);

        Assert.False(result.IsSuccess);
        Assert.Equal("NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task UnreachableRoomService_IsAnInternalError()
    {
        _roomClient.Error = new RpcException(new Status(StatusCode.Unavailable, "down"));

        var result = await _policy.GetAsync(RoomId);

        Assert.False(result.IsSuccess);
        Assert.Equal("INTERNAL_ERROR", result.ErrorCode);
    }

    [Theory]
    [InlineData("vi", true)]
    [InlineData("EN", true)]
    [InlineData("ja", false)]
    [InlineData("klingon", false)]
    [InlineData("vi-VN", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowed_AcceptsOnlyBareCodesInTheSet(string? code, bool expected)
    {
        var snapshot = new TranscriptRoomLanguageSnapshot(EffectiveHostId, ["vi", "en", "klingon"]);

        Assert.Equal(expected, snapshot.IsAllowed(code));
    }

    private static GetTranslationRoomResponse Room(
        string source,
        IEnumerable<string> targets,
        IEnumerable<string> generatable,
        bool resolved)
    {
        var response = new GetTranslationRoomResponse
        {
            Id = RoomId.ToString(),
            HostId = BookerId.ToString(),
            EffectiveHostId = EffectiveHostId.ToString(),
            Status = "ENDED",
            SourceLanguage = source,
            ArtifactLanguagesResolved = resolved
        };
        response.TargetLanguages.AddRange(targets);
        response.GeneratableArtifactLanguages.AddRange(generatable);
        return response;
    }

    private sealed class FakeRoomClient : TranslationRoomService.TranslationRoomServiceClient
    {
        public GetTranslationRoomResponse? Response { get; set; }

        public RpcException? Error { get; set; }

        public GetTranslationRoomRequest? LastRequest { get; private set; }

        public override AsyncUnaryCall<GetTranslationRoomResponse> GetTranslationRoomByIdAsync(
            GetTranslationRoomRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;

            var response = Error is null
                ? Task.FromResult(Response ?? throw new InvalidOperationException("No response configured."))
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

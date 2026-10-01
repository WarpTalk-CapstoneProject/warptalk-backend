using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Gateway.Services;
using WarpTalk.Shared.Protos;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// WT-709: the hub's language predicate answers against BOTH limits — the meeting's own
/// languages (L2) and the workspace whitelist (L1) — and says which one refused.
/// </summary>
public class RoomLanguagePolicyTests
{
    private readonly Mock<WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient> _rooms = new();
    private readonly Mock<WorkspaceService.WorkspaceServiceClient> _workspaces = new();
    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid WorkspaceId = Guid.NewGuid();

    private static AsyncUnaryCall<T> Call<T>(T response) =>
        new(Task.FromResult(response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

    private RoomLanguagePolicy Policy(string type, string source, string[] targets, params string[] allowed)
    {
        var room = new GetTranslationRoomResponse
        {
            Id = RoomId.ToString(),
            WorkspaceId = WorkspaceId.ToString(),
            TranslationRoomType = type,
            SourceLanguage = source,
        };
        room.TargetLanguages.AddRange(targets);
        _rooms
            .Setup(c => c.GetTranslationRoomByIdAsync(
                It.IsAny<GetTranslationRoomRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(Call(room));

        var settings = new GetWorkspaceSettingsResponse();
        settings.AllowedTargetLanguages.AddRange(allowed);
        _workspaces
            .Setup(c => c.GetWorkspaceSettingsAsync(
                It.IsAny<GetWorkspaceSettingsRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(Call(settings));

        return new RoomLanguagePolicy(_rooms.Object, _workspaces.Object, NullLogger<RoomLanguagePolicy>.Instance);
    }

    [Fact]
    public async Task A_language_the_meeting_declares_is_allowed_whatever_its_locale_tag()
    {
        var policy = Policy("EVENT", "vi", new[] { "en" }, "vi", "en", "ko");

        Assert.Equal(RoomLanguageVerdict.Allowed, await policy.EvaluateLanguageAsync(RoomId, "en-US"));
    }

    [Fact]
    public async Task A_language_outside_the_meeting_is_refused_as_not_in_the_room_even_when_the_workspace_allows_it()
    {
        var policy = Policy("EVENT", "vi", new[] { "en" }, "vi", "en", "ko");

        Assert.Equal(RoomLanguageVerdict.NotInRoomLanguages, await policy.EvaluateLanguageAsync(RoomId, "ko"));
    }

    [Fact]
    public async Task A_meeting_language_the_workspace_has_since_dropped_is_refused_by_the_workspace()
    {
        var policy = Policy("EVENT", "vi", new[] { "en" }, "vi");

        Assert.Equal(RoomLanguageVerdict.NotInWorkspacePolicy, await policy.EvaluateLanguageAsync(RoomId, "en"));
    }

    [Fact]
    public async Task A_room_with_no_declared_languages_is_judged_by_the_workspace_alone()
    {
        var policy = Policy("EVENT", "", Array.Empty<string>());

        Assert.Equal(RoomLanguageVerdict.Allowed, await policy.EvaluateLanguageAsync(RoomId, "ko"));
    }

    /// <summary>
    /// A bridge room is shared by everyone in a Meet call with whatever language they picked, and
    /// its far side speaks whatever Meet speaks — it has no L2 to hold anyone to. L1 still holds.
    /// </summary>
    [Fact]
    public async Task A_bridge_room_skips_the_meeting_limit_but_not_the_workspace_one()
    {
        var policy = Policy("EXTERNAL_BRIDGE", "vi", new[] { "en" }, "vi", "en", "ko");
        Assert.Equal(RoomLanguageVerdict.Allowed, await policy.EvaluateLanguageAsync(RoomId, "ko"));

        var narrowed = Policy("EXTERNAL_BRIDGE", "vi", new[] { "en" }, "vi", "en");
        Assert.Equal(RoomLanguageVerdict.NotInWorkspacePolicy, await narrowed.EvaluateLanguageAsync(RoomId, "ko"));
    }
}

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-709: a participant chooses within the meeting's own languages (L2), and the host can add a
/// language to a running meeting — still inside the workspace whitelist (L1) and the plan quota.
///
/// The two halves only make sense together. The restored rule alone would turn away the guest who
/// needs Korean in a vi/en meeting; the add alone would be a door with no wall beside it.
/// </summary>
public class RoomLanguagesInMeetingTests
{
    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();
    private static readonly Guid WorkspaceId = Guid.NewGuid();

    private static TranslationRoom Room(string status = "IN_PROGRESS", string type = "EVENT") => new()
    {
        Id = RoomId,
        WorkspaceId = WorkspaceId,
        HostId = HostId,
        Title = "Weekly sync",
        TranslationRoomCode = "abc-defg-hij",
        Status = status,
        TranslationRoomType = type,
        SourceLanguage = "vi",
        TargetLanguages = "[\"en\"]",
        Settings = "{\"requires_approval\":false}",
    };

    // ── the rule: LanguagePolicy.ValidateParticipantLanguagesAsync ─────────────────

    private static LanguagePolicy RealPolicy()
    {
        var languages = new Mock<ILanguageRepository>();
        languages.Setup(r => r.IsSupportedAsync(It.IsAny<string>())).ReturnsAsync(true);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.LanguageRepository).Returns(languages.Object);
        return new LanguagePolicy(unitOfWork.Object);
    }

    [Fact]
    public async Task A_participant_on_the_meetings_languages_passes_whatever_the_locale_tag()
    {
        var error = await RealPolicy().ValidateParticipantLanguagesAsync("en-US", "vi-VN", Room());

        error.Should().BeNull();
    }

    [Theory]
    [InlineData("ko", "vi")]
    [InlineData("vi", "ko")]
    public async Task A_language_the_meeting_does_not_declare_is_refused_and_names_the_way_out(string speak, string listen)
    {
        var error = await RealPolicy().ValidateParticipantLanguagesAsync(speak, listen, Room());

        error.Should().NotBeNull();
        error.Should().Contain("'ko'").And.Contain("vi, en").And.Contain("Ask the host");
    }

    /// <summary>
    /// A bridge room is shared by everyone in one Meet call with whatever "My language" they
    /// picked, so there is no L2 to hold them to. L1 is enforced for it elsewhere (hub + join).
    /// </summary>
    [Fact]
    public async Task A_bridge_room_places_no_meeting_limit_on_its_participants()
    {
        var error = await RealPolicy().ValidateParticipantLanguagesAsync("ko", "ja", Room(type: "EXTERNAL_BRIDGE"));

        error.Should().BeNull();
    }

    [Fact]
    public async Task A_room_with_no_declared_languages_is_not_read_as_allowing_nothing()
    {
        var room = Room();
        room.SourceLanguage = "";
        room.TargetLanguages = "[]";

        var error = await RealPolicy().ValidateParticipantLanguagesAsync("ko", "ja", room);

        error.Should().BeNull();
    }

    // ── the door: AddRoomLanguageAsync ────────────────────────────────────────────

    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ITranslationRoomRepository> _rooms = new();
    private readonly Mock<IWorkspaceMeetingPolicy> _workspacePolicy = new();
    private readonly Mock<ILanguagePolicy> _languagePolicy = new();
    private readonly Mock<IUserSettingsDirectory> _userSettings = new();
    private readonly Mock<IRedisStateRepository> _redis = new();
    private readonly Dictionary<string, string> _stored = new(StringComparer.Ordinal);
    private readonly List<string> _published = new();

    private WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService Service(TranslationRoom room)
    {
        _unitOfWork.Setup(u => u.TranslationRoomRepository).Returns(_rooms.Object);
        _rooms.Setup(r => r.GetByIdAsync(room.Id, It.IsAny<CancellationToken>())).ReturnsAsync(room);

        _languagePolicy.Setup(p => p.IsSupportedAsync(It.IsAny<string>())).ReturnsAsync(true);
        _workspacePolicy
            .Setup(p => p.ValidateRoomLanguagesAsync(
                It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        _redis
            .Setup(r => r.StringSetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()))
            .ReturnsAsync((string key, string value, TimeSpan? _) =>
            {
                _stored[key] = value;
                return true;
            });
        _redis
            .Setup(r => r.PublishAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string _, string message) =>
            {
                _published.Add(message);
                return 1L;
            });

        return new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(
            _unitOfWork.Object,
            _languagePolicy.Object,
            Mock.Of<IAudioRouteEventProcessor>(),
            Mock.Of<ITranslationRoomAudioRouteService>(),
            _userSettings.Object,
            _workspacePolicy.Object,
            Mock.Of<IWorkspaceMemberDirectory>(),
            Mock.Of<WarpTalk.Shared.Interfaces.IEmailService>(),
            Mock.Of<ILogger<WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>>(),
            redisStateRepository: _redis.Object);
    }

    [Fact]
    public async Task The_host_adds_a_language_and_it_is_saved_published_to_the_workers_and_broadcast()
    {
        var room = Room();
        var service = Service(room);

        var result = await service.AddRoomLanguageAsync(RoomId, HostId, "ko-KR");

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.SourceLanguage.Should().Be("vi");
        result.Value.TargetLanguages.Should().Equal("en", "ko");
        room.TargetLanguages.Should().Contain("ko");
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

        JsonSerializer.Deserialize<List<string>>(_stored[$"meeting:{RoomId}:target_languages"])
            .Should().Equal("en", "ko");
        _published.Should().Contain(m => m.Contains("RoomLanguagesChanged") && m.Contains("\"ko\""));

        // The quota counts the SET, so the gate is handed the whole new target list.
        _workspacePolicy.Verify(p => p.ValidateRoomLanguagesAsync(
            WorkspaceId, "vi",
            It.Is<IEnumerable<string>>(t => string.Join(",", t) == "en,ko"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Only_the_host_may_add_a_language()
    {
        var room = Room();
        var result = await Service(room).AddRoomLanguageAsync(RoomId, Guid.NewGuid(), "ko");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Unauthorized);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("SCHEDULED")]
    [InlineData("ENDED")]
    public async Task A_meeting_that_is_not_open_takes_its_languages_from_the_settings_form_instead(string status)
    {
        var result = await Service(Room(status)).AddRoomLanguageAsync(RoomId, HostId, "ko");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidState);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("vi-VN")]
    public async Task A_language_the_meeting_already_has_is_a_no_op_success(string language)
    {
        var result = await Service(Room()).AddRoomLanguageAsync(RoomId, HostId, language);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.TargetLanguages.Should().Equal("en");
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task The_workspace_whitelist_and_quota_still_bound_what_the_host_can_add()
    {
        var room = Room();
        var service = Service(room);
        _workspacePolicy
            .Setup(p => p.ValidateRoomLanguagesAsync(
                It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure("This workspace does not allow 'ko' in meetings.", ErrorCodes.ValidationError));

        var result = await service.AddRoomLanguageAsync(RoomId, HostId, "ko");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("'ko'");
        room.TargetLanguages.Should().Be("[\"en\"]");
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _published.Should().BeEmpty();
    }

    /// <summary>
    /// WT-708 × WT-709. The room was booked vi→en,es; the workspace has since dropped es. Adding ko
    /// must not be refused over es, and must not republish es to the workers either — while the
    /// booking itself keeps es, so widening L1 again restores it.
    /// </summary>
    [Fact]
    public async Task Adding_to_a_room_the_workspace_has_since_narrowed_works_from_what_it_runs_in()
    {
        var room = Room();
        room.TargetLanguages = "[\"en\",\"es\"]";
        var service = Service(room);
        _workspacePolicy
            .Setup(p => p.GetAllowedLanguagesAsync(WorkspaceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<string>>(new[] { "vi", "en", "ko" }));

        var result = await service.AddRoomLanguageAsync(RoomId, HostId, "ko");

        result.IsSuccess.Should().BeTrue(result.Error);
        _workspacePolicy.Verify(p => p.ValidateRoomLanguagesAsync(
            WorkspaceId, "vi",
            It.Is<IEnumerable<string>>(t => string.Join(",", t) == "en,ko"),
            It.IsAny<CancellationToken>()), Times.Once);
        JsonSerializer.Deserialize<List<string>>(_stored[$"meeting:{RoomId}:target_languages"])
            .Should().Equal("en", "ko");
        room.TargetLanguages.Should().Contain("es").And.Contain("ko");
    }

    // ── the join: a profile default is not a choice ───────────────────────────────

    /// <summary>
    /// A joiner who picked nothing gets their profile defaults — and a default outside the
    /// meeting's languages falls back to the meeting's source language instead of being refused
    /// with "ask the host" for a choice they never made.
    /// </summary>
    [Fact]
    public async Task A_profile_default_outside_the_meeting_falls_back_to_its_source_language()
    {
        var room = Room("WAITING");
        var service = Service(room);
        _rooms.Setup(r => r.GetByCodeAsync(room.TranslationRoomCode, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _workspacePolicy
            .Setup(p => p.EnsureWorkspaceCanHostMeetingsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        _userSettings
            .Setup(s => s.GetDefaultsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserLanguageDefaults("ko", "en"));

        string? seenSpeak = null, seenListen = null;
        _languagePolicy
            .Setup(p => p.ValidateParticipantLanguagesAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<TranslationRoom>()))
            .Callback((string? speak, string? listen, TranslationRoom _) => { seenSpeak = speak; seenListen = listen; })
            .ReturnsAsync("stop here");

        await service.JoinTranslationRoomAsync(
            new JoinTranslationRoomRequest(room.TranslationRoomCode, "Guest", null, null), Guid.NewGuid());

        seenSpeak.Should().Be("vi", "ko is not one of this meeting's languages and the joiner never chose it");
        seenListen.Should().Be("en", "a default that IS a meeting language is kept");
    }
}

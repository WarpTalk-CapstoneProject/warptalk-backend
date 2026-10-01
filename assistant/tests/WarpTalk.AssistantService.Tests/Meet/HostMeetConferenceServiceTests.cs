using System.Linq.Expressions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Tests.Meet;

public class HostMeetConferenceServiceTests
{
    private static readonly Guid HostId = Guid.NewGuid();
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private const string Meeting = "https://meet.google.com/abc-mnop-xyz?authuser=0";
    private const string Code = "abc-mnop-xyz";
    private static readonly DateTimeOffset T9 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPluginConnectionRepository _connections = Substitute.For<IPluginConnectionRepository>();
    private readonly IPluginRepository _plugins = Substitute.For<IPluginRepository>();
    private readonly IPluginInstallationRepository _installations = Substitute.For<IPluginInstallationRepository>();
    private readonly IWorkspacePluginGuard _guard = Substitute.For<IWorkspacePluginGuard>();
    private readonly IPluginTokenRefresher _refresher = Substitute.For<IPluginTokenRefresher>();
    private readonly IPluginCredentialProtector _protector = Substitute.For<IPluginCredentialProtector>();
    private readonly IGoogleMeetRestClient _meet = Substitute.For<IGoogleMeetRestClient>();
    private readonly PluginConnection _connection = new()
    {
        UserId = HostId,
        Provider = PluginConstants.Providers.Google,
        Status = PluginConstants.ConnectionStatus.Connected,
        ScopesJson = $"[\"https://www.googleapis.com/auth/calendar.events\",\"{MeetConferenceErrorCodes.MeetSpaceReadonlyScope}\"]",
        EncryptedAccessToken = "enc-1",
        AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1),
    };
    private readonly Plugin _meetPlugin = new()
    {
        Id = Guid.NewGuid(),
        PluginKey = "google_meet",
        Provider = PluginConstants.Providers.Google,
        IsActive = true,
    };
    private PluginInstallation? _installation;

    public HostMeetConferenceServiceTests()
    {
        _unitOfWork.PluginConnectionRepository.Returns(_connections);
        _unitOfWork.PluginRepository.Returns(_plugins);
        _connections.FirstOrDefaultAsync(Arg.Any<Expression<Func<PluginConnection, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Expression<Func<PluginConnection, bool>>>().Compile()(_connection) ? _connection : null);
        _plugins.FirstOrDefaultAsync(Arg.Any<Expression<Func<Plugin, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_meetPlugin);
        _unitOfWork.PluginInstallationRepository.Returns(_installations);
        _installations.FirstOrDefaultAsync(Arg.Any<Expression<Func<PluginInstallation, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _installation is not null && call.Arg<Expression<Func<PluginInstallation, bool>>>().Compile()(_installation)
                ? _installation
                : null);
        // The host has installed AND connected google_meet, and it is usable in the room's workspace.
        _installation = new PluginInstallation
        {
            UserId = HostId,
            PluginId = _meetPlugin.Id,
            Status = PluginConstants.InstallationStatus.Installed,
            ConnectedAt = DateTime.UtcNow.AddDays(-1),
        };
        _guard.CanUsePluginInWorkspaceAsync(WorkspaceId, HostId, _meetPlugin, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _guard.CanUsePluginInWorkspaceAsync(Arg.Is<Guid?>(id => id != WorkspaceId), Arg.Any<Guid>(), Arg.Any<Plugin>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Workspace required.", PluginConstants.ErrorCodes.PermissionDenied));
        _protector.Unprotect("enc-1").Returns("token-1");
        _protector.Unprotect("enc-2").Returns("token-2");
    }

    private HostMeetConferenceService Sut() =>
        new(_unitOfWork, _refresher, _protector, _meet, _guard, NullLogger<HostMeetConferenceService>.Instance);

    private static MeetRestResult<IReadOnlyList<T>> Ok<T>(params T[] items) => MeetRestResult<IReadOnlyList<T>>.Success(items);

    /// <summary>Each of the three reads, so every Meet REST path is held to the plugin rule.</summary>
    public static TheoryData<string> Reads => new() { "records", "roster", "entries" };

    private Task<string?> ReadAsync(string read, Guid? workspaceId = null)
    {
        var sut = Sut();
        var ws = workspaceId ?? WorkspaceId;
        return read switch
        {
            "records" => sut.GetConferenceRecordsAsync(HostId, ws, Meeting).ContinueWith(t => t.Result.ErrorCode),
            "roster" => sut.GetRosterAsync(HostId, ws, Meeting).ContinueWith(t => t.Result.ErrorCode),
            _ => sut.GetTranscriptEntriesAsync(HostId, ws, Meeting, T9, T9.AddHours(1)).ContinueWith(t => t.Result.ErrorCode),
        };
    }

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task PluginNotInstalled_IsPluginNotConnected_WithoutCallingGoogle(string read)
    {
        _installation = null;

        Assert.Equal(MeetConferenceErrorCodes.PluginNotConnected, await ReadAsync(read));
        await _meet.DidNotReceiveWithAnyArgs().FindConferenceRecordsAsync(default!, default!, default);
    }

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task InstalledButNeverConnected_IsPluginNotConnected_EvenWithAGrantThatHasTheScope(string read)
    {
        // The user connected Calendar: the provider grant is live and even carries the Meet scope.
        // That does not connect google_meet.
        _installation!.ConnectedAt = null;

        Assert.Equal(MeetConferenceErrorCodes.PluginNotConnected, await ReadAsync(read));
        await _meet.DidNotReceiveWithAnyArgs().FindConferenceRecordsAsync(default!, default!, default);
    }

    [Fact]
    public async Task DisabledInstallation_IsPluginNotConnected()
    {
        _installation!.Status = PluginConstants.InstallationStatus.Disabled;

        Assert.Equal(MeetConferenceErrorCodes.PluginNotConnected, await ReadAsync("records"));
    }

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task PluginNotUsableInTheRoomsWorkspace_IsPluginNotConnected(string read)
    {
        Assert.Equal(MeetConferenceErrorCodes.PluginNotConnected, await ReadAsync(read, Guid.NewGuid()));
        await _meet.DidNotReceiveWithAnyArgs().FindConferenceRecordsAsync(default!, default!, default);
    }

    [Fact]
    public async Task NoGoogleMeetPluginInTheCatalog_IsPluginNotConnected()
    {
        _plugins.FirstOrDefaultAsync(Arg.Any<Expression<Func<Plugin, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Plugin?)null);

        Assert.Equal(MeetConferenceErrorCodes.PluginNotConnected, await ReadAsync("records"));
    }

    [Fact]
    public async Task PluginNotConnected_IsReportedBeforeAMissingGrantOrScope()
    {
        // Connecting the plugin is the one action that fixes all three, so it is the answer given.
        _installation = null;
        _connection.Status = PluginConstants.ConnectionStatus.Expired;
        _connection.ScopesJson = "[]";

        Assert.Equal(MeetConferenceErrorCodes.PluginNotConnected, await ReadAsync("records"));
    }

    [Fact]
    public async Task PluginConnected_ButGrantWithoutTheScope_IsStillMeetScopeMissing()
    {
        _connection.ScopesJson = "[\"https://www.googleapis.com/auth/calendar.events\"]";

        Assert.Equal(MeetConferenceErrorCodes.MeetScopeMissing, await ReadAsync("roster"));
    }

    [Fact]
    public async Task NoGoogleConnection_IsConnectionRequired()
    {
        _connection.Status = PluginConstants.ConnectionStatus.Expired;

        var result = await Sut().GetConferenceRecordsAsync(HostId, WorkspaceId, Meeting);

        Assert.Equal(MeetConferenceErrorCodes.ConnectionRequired, result.ErrorCode);
        await _meet.DidNotReceiveWithAnyArgs().FindConferenceRecordsAsync(default!, default!, default);
    }

    [Fact]
    public async Task GrantWithoutTheMeetScope_IsMeetScopeMissing_WithoutCallingGoogle()
    {
        _connection.ScopesJson = "[\"https://www.googleapis.com/auth/calendar.events\"]";

        var result = await Sut().GetConferenceRecordsAsync(HostId, WorkspaceId, Meeting);

        Assert.Equal(MeetConferenceErrorCodes.MeetScopeMissing, result.ErrorCode);
        await _meet.DidNotReceiveWithAnyArgs().FindConferenceRecordsAsync(default!, default!, default);
    }

    [Fact]
    public async Task NotAMeetLink_IsInvalidMeeting()
    {
        var result = await Sut().GetConferenceRecordsAsync(HostId, WorkspaceId, "https://zoom.us/j/123");

        Assert.Equal(MeetConferenceErrorCodes.InvalidMeeting, result.ErrorCode);
    }

    [Fact]
    public async Task UsesTheNormalizedCodeAndTheDecryptedToken()
    {
        _meet.FindConferenceRecordsAsync("token-1", Code, Arg.Any<CancellationToken>())
            .Returns(Ok(new MeetConferenceRecordDto("conferenceRecords/r1", T9, null)));

        var result = await Sut().GetConferenceRecordsAsync(HostId, WorkspaceId, Meeting);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!);
    }

    [Fact]
    public async Task ExpiringToken_IsRefreshedFirst()
    {
        _connection.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(10);
        _refresher.RefreshAccessTokenAsync(_meetPlugin, _connection, Arg.Any<CancellationToken>())
            .Returns(_ => { _connection.EncryptedAccessToken = "enc-2"; return Result.Success(); });
        _meet.FindConferenceRecordsAsync("token-2", Code, Arg.Any<CancellationToken>()).Returns(Ok<MeetConferenceRecordDto>());

        var result = await Sut().GetConferenceRecordsAsync(HostId, WorkspaceId, Meeting);

        Assert.True(result.IsSuccess);
        await _meet.DidNotReceive().FindConferenceRecordsAsync("token-1", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Google401_RefreshesOnceAndRetries()
    {
        _meet.FindConferenceRecordsAsync("token-1", Code, Arg.Any<CancellationToken>())
            .Returns(MeetRestResult<IReadOnlyList<MeetConferenceRecordDto>>.Failure(MeetRestErrorKind.Unauthorized));
        _refresher.RefreshAccessTokenAsync(_meetPlugin, _connection, Arg.Any<CancellationToken>())
            .Returns(_ => { _connection.EncryptedAccessToken = "enc-2"; return Result.Success(); });
        _meet.FindConferenceRecordsAsync("token-2", Code, Arg.Any<CancellationToken>())
            .Returns(Ok(new MeetConferenceRecordDto("conferenceRecords/r1", T9, T9.AddHours(1))));

        var result = await Sut().GetConferenceRecordsAsync(HostId, WorkspaceId, Meeting);

        Assert.True(result.IsSuccess);
        await _refresher.Received(1).RefreshAccessTokenAsync(_meetPlugin, _connection, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshRejected_IsConnectionRequired()
    {
        _meet.FindConferenceRecordsAsync("token-1", Code, Arg.Any<CancellationToken>())
            .Returns(MeetRestResult<IReadOnlyList<MeetConferenceRecordDto>>.Failure(MeetRestErrorKind.Unauthorized));
        _refresher.RefreshAccessTokenAsync(_meetPlugin, _connection, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("gone", PluginConstants.ErrorCodes.ConnectionRequired));

        var result = await Sut().GetConferenceRecordsAsync(HostId, WorkspaceId, Meeting);

        Assert.Equal(MeetConferenceErrorCodes.ConnectionRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Google403InsufficientScope_IsMeetScopeMissing()
    {
        _meet.FindConferenceRecordsAsync("token-1", Code, Arg.Any<CancellationToken>())
            .Returns(MeetRestResult<IReadOnlyList<MeetConferenceRecordDto>>.Failure(MeetRestErrorKind.ScopeMissing));

        var result = await Sut().GetConferenceRecordsAsync(HostId, WorkspaceId, Meeting);

        Assert.Equal(MeetConferenceErrorCodes.MeetScopeMissing, result.ErrorCode);
    }

    [Fact]
    public async Task Roster_PrefersTheLiveConference()
    {
        _meet.FindConferenceRecordsAsync("token-1", Code, Arg.Any<CancellationToken>()).Returns(Ok(
            new MeetConferenceRecordDto("conferenceRecords/old", T9.AddHours(-3), T9.AddHours(-2)),
            new MeetConferenceRecordDto("conferenceRecords/live", T9, null)));
        _meet.ListParticipantsAsync("token-1", "conferenceRecords/live", Arg.Any<CancellationToken>()).Returns(Ok(
            new MeetParticipantDto("conferenceRecords/live/participants/2", "Bob", MeetParticipantKinds.Anonymous, T9.AddMinutes(5), null),
            new MeetParticipantDto("conferenceRecords/live/participants/1", "Alice", MeetParticipantKinds.SignedIn, T9, null)));

        var result = await Sut().GetRosterAsync(HostId, WorkspaceId, Meeting);

        Assert.True(result.IsSuccess);
        Assert.Equal("conferenceRecords/live", result.Value!.ConferenceRecord);
        Assert.Equal(new[] { "Alice", "Bob" }, result.Value.Participants.Select(p => p.DisplayName));
    }

    [Fact]
    public async Task Roster_NoConferenceYet_IsEmpty()
    {
        _meet.FindConferenceRecordsAsync("token-1", Code, Arg.Any<CancellationToken>()).Returns(Ok<MeetConferenceRecordDto>());

        var result = await Sut().GetRosterAsync(HostId, WorkspaceId, Meeting);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Participants);
    }

    [Fact]
    public void PickRosterRecord_FallsBackToTheLatestEnded()
    {
        var picked = HostMeetConferenceService.PickRosterRecord(
        [
            new MeetConferenceRecordDto("conferenceRecords/a", T9.AddHours(-5), T9.AddHours(-4)),
            new MeetConferenceRecordDto("conferenceRecords/b", T9.AddHours(-1), T9),
        ]);

        Assert.Equal("conferenceRecords/b", picked!.Name);
    }

    [Fact]
    public async Task TranscriptEntries_OnlyOverlappingRecords_NamesResolved_PendingCounted()
    {
        _meet.FindConferenceRecordsAsync("token-1", Code, Arg.Any<CancellationToken>()).Returns(Ok(
            new MeetConferenceRecordDto("conferenceRecords/yesterday", T9.AddDays(-1), T9.AddDays(-1).AddHours(1)),
            new MeetConferenceRecordDto("conferenceRecords/r1", T9, T9.AddHours(1))));
        _meet.ListTranscriptsAsync("token-1", "conferenceRecords/r1", Arg.Any<CancellationToken>()).Returns(Ok(
            new MeetTranscriptDto("conferenceRecords/r1/transcripts/t1", "FILE_GENERATED"),
            new MeetTranscriptDto("conferenceRecords/r1/transcripts/t2", "STARTED")));
        _meet.ListParticipantsAsync("token-1", "conferenceRecords/r1", Arg.Any<CancellationToken>()).Returns(Ok(
            new MeetParticipantDto("conferenceRecords/r1/participants/1", "Alice", MeetParticipantKinds.SignedIn, T9, T9.AddHours(1))));
        _meet.ListTranscriptEntriesAsync("token-1", "conferenceRecords/r1/transcripts/t1", Arg.Any<CancellationToken>()).Returns(Ok(
            new MeetTranscriptEntryDto("e2", "conferenceRecords/r1/participants/1", "second", "en", T9.AddSeconds(10), T9.AddSeconds(12)),
            new MeetTranscriptEntryDto("e1", "conferenceRecords/r1/participants/1", "first", "en", T9.AddSeconds(1), T9.AddSeconds(3)),
            new MeetTranscriptEntryDto("e3", "conferenceRecords/r1/participants/9", "who", "en", T9.AddSeconds(20), T9.AddSeconds(21))));

        var result = await Sut().GetTranscriptEntriesAsync(HostId, WorkspaceId, Meeting, T9.AddMinutes(-10), T9.AddHours(2));

        Assert.True(result.IsSuccess);
        var value = result.Value!;
        Assert.Equal(1, value.ConferenceRecords);
        Assert.Equal(1, value.TranscriptsReady);
        Assert.Equal(1, value.TranscriptsPending);
        Assert.False(value.ConferenceLive);
        Assert.Equal(new[] { "first", "second", "who" }, value.Entries.Select(e => e.Text));
        Assert.Equal("Alice", value.Entries[0].DisplayName);
        Assert.Equal(string.Empty, value.Entries[2].DisplayName);
        await _meet.DidNotReceive().ListTranscriptsAsync(Arg.Any<string>(), "conferenceRecords/yesterday", Arg.Any<CancellationToken>());
        await _meet.DidNotReceive().ListTranscriptEntriesAsync(Arg.Any<string>(), "conferenceRecords/r1/transcripts/t2", Arg.Any<CancellationToken>());
    }
}

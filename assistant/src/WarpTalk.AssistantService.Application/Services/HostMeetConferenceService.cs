using Microsoft.Extensions.Logging;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

/// <inheritdoc cref="IHostMeetConferenceService"/>
public sealed class HostMeetConferenceService : IHostMeetConferenceService
{
    /// <summary>The catalog row whose OAuth client refreshes the grant. Any Google row would do.</summary>
    public const string GoogleMeetPluginKey = "google_meet";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IPluginTokenRefresher _tokenRefresher;
    private readonly IPluginCredentialProtector _credentialProtector;
    private readonly IGoogleMeetRestClient _meet;
    private readonly IWorkspacePluginGuard _workspacePlugins;
    private readonly ILogger<HostMeetConferenceService> _logger;

    public HostMeetConferenceService(
        IUnitOfWork unitOfWork,
        IPluginTokenRefresher tokenRefresher,
        IPluginCredentialProtector credentialProtector,
        IGoogleMeetRestClient meet,
        IWorkspacePluginGuard workspacePlugins,
        ILogger<HostMeetConferenceService> logger)
    {
        _unitOfWork = unitOfWork;
        _tokenRefresher = tokenRefresher;
        _credentialProtector = credentialProtector;
        _meet = meet;
        _workspacePlugins = workspacePlugins;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<MeetConferenceRecordDto>>> GetConferenceRecordsAsync(
        Guid userId, Guid? workspaceId, string meeting, CancellationToken ct = default)
    {
        if (!GoogleMeetCodeParser.TryParse(meeting, out var code))
            return InvalidMeeting<IReadOnlyList<MeetConferenceRecordDto>>();

        return await WithUserTokenAsync(userId, workspaceId, token => _meet.FindConferenceRecordsAsync(token, code, ct), ct);
    }

    public async Task<Result<MeetRosterDto>> GetRosterAsync(
        Guid userId, Guid? workspaceId, string meeting, CancellationToken ct = default)
    {
        if (!GoogleMeetCodeParser.TryParse(meeting, out var code))
            return InvalidMeeting<MeetRosterDto>();

        return await WithUserTokenAsync(userId, workspaceId, async token =>
        {
            var records = await _meet.FindConferenceRecordsAsync(token, code, ct);
            if (!records.IsSuccess) return records.As<MeetRosterDto>();

            var record = PickRosterRecord(records.Value!);
            if (record is null)
                return MeetRestResult<MeetRosterDto>.Success(new MeetRosterDto(string.Empty, Array.Empty<MeetParticipantDto>()));

            var participants = await _meet.ListParticipantsAsync(token, record.Name, ct);
            return participants.IsSuccess
                ? MeetRestResult<MeetRosterDto>.Success(new MeetRosterDto(
                    record.Name,
                    participants.Value!.OrderBy(p => p.JoinedAt ?? DateTimeOffset.MaxValue).ToList()))
                : participants.As<MeetRosterDto>();
        }, ct);
    }

    public async Task<Result<MeetTranscriptEntriesDto>> GetTranscriptEntriesAsync(
        Guid userId, Guid? workspaceId, string meeting, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default)
    {
        if (!GoogleMeetCodeParser.TryParse(meeting, out var code))
            return InvalidMeeting<MeetTranscriptEntriesDto>();

        return await WithUserTokenAsync(userId, workspaceId, async token =>
        {
            var records = await _meet.FindConferenceRecordsAsync(token, code, ct);
            if (!records.IsSuccess) return records.As<MeetTranscriptEntriesDto>();

            var overlapping = records.Value!
                .Where(r => Overlaps(r, windowStart, windowEnd))
                .OrderBy(r => r.StartTime ?? DateTimeOffset.MinValue)
                .ToList();

            var ready = 0;
            var pending = 0;
            var lines = new List<MeetTranscriptLineDto>();
            foreach (var record in overlapping)
            {
                var transcripts = await _meet.ListTranscriptsAsync(token, record.Name, ct);
                if (!transcripts.IsSuccess) return transcripts.As<MeetTranscriptEntriesDto>();

                var readable = transcripts.Value!.Where(t => t.IsReadable).ToList();
                ready += readable.Count;
                pending += transcripts.Value!.Count - readable.Count;
                if (readable.Count == 0) continue;

                var participants = await _meet.ListParticipantsAsync(token, record.Name, ct);
                if (!participants.IsSuccess) return participants.As<MeetTranscriptEntriesDto>();
                var names = participants.Value!
                    .GroupBy(p => p.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().DisplayName, StringComparer.Ordinal);

                foreach (var transcript in readable)
                {
                    var entries = await _meet.ListTranscriptEntriesAsync(token, transcript.Name, ct);
                    if (!entries.IsSuccess) return entries.As<MeetTranscriptEntriesDto>();

                    lines.AddRange(entries.Value!
                        .Where(e => !string.IsNullOrWhiteSpace(e.Text))
                        .Select(e => new MeetTranscriptLineDto(
                            e.Participant,
                            names.GetValueOrDefault(e.Participant) ?? string.Empty,
                            e.Text,
                            e.LanguageCode,
                            e.StartTime,
                            e.EndTime)));
                }
            }

            return MeetRestResult<MeetTranscriptEntriesDto>.Success(new MeetTranscriptEntriesDto(
                overlapping.Count,
                ready,
                pending,
                overlapping.Any(r => r.EndTime is null),
                lines.OrderBy(l => l.StartTime ?? DateTimeOffset.MinValue).ToList()));
        }, ct);
    }

    /// <summary>The live record if there is one (latest started), otherwise the latest ended one.</summary>
    public static MeetConferenceRecordDto? PickRosterRecord(IReadOnlyList<MeetConferenceRecordDto> records) =>
        records
            .OrderBy(r => r.EndTime is null ? 0 : 1)
            .ThenByDescending(r => r.StartTime ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

    private static bool Overlaps(MeetConferenceRecordDto record, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var start = record.StartTime ?? DateTimeOffset.MinValue;
        var end = record.EndTime ?? DateTimeOffset.MaxValue;
        return start <= windowEnd && end >= windowStart;
    }

    /// <summary>
    /// Checks the user may be read for at all, resolves their Google access token, runs
    /// <paramref name="call"/> with it, and on a 401 refreshes once and runs it again — the same
    /// proactive + reactive refresh WarpBot's tool calls use, through the same
    /// <see cref="IPluginTokenRefresher"/>.
    /// </summary>
    /// <remarks>
    /// The checks, in order, each with its own code so the caller can say what to do about it:
    /// <list type="number">
    /// <item><c>plugin_not_connected</c> — the google_meet plugin is not installed and connected by
    /// the user, or not usable in <paramref name="workspaceId"/>. First, because connecting the
    /// plugin is the one action that also produces the grant and its scope: telling a user with no
    /// grant at all to "reconnect Google" would send them to the wrong place.</item>
    /// <item><c>connection_required</c> — the plugin was connected but the provider grant is gone
    /// (revoked, expired, never stored).</item>
    /// <item><c>meet_scope_missing</c> — the grant does not carry meetings.space.readonly.</item>
    /// </list>
    /// A Google grant obtained through Calendar or Drive does not count: "connected" is per plugin
    /// installation (<see cref="PluginInstallation.ConnectedAt"/>), the grant is per provider.
    /// </remarks>
    private async Task<Result<T>> WithUserTokenAsync<T>(
        Guid userId,
        Guid? workspaceId,
        Func<string, Task<MeetRestResult<T>>> call,
        CancellationToken ct)
    {
        var plugin = await GooglePluginAsync(ct);
        var notConnected = await CheckPluginConnectedAsync<T>(plugin, userId, workspaceId, ct);
        if (notConnected is not null) return notConnected;

        var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
            c => c.UserId == userId && c.Provider == PluginConstants.Providers.Google,
            ct: ct);
        if (connection is null || connection.Status != PluginConstants.ConnectionStatus.Connected)
            return Result.Failure<T>("The host has no connected Google account.", MeetConferenceErrorCodes.ConnectionRequired);

        var scopes = PluginScopeMapper.FromJson(connection.ScopesJson);
        if (!scopes.Contains(MeetConferenceErrorCodes.MeetSpaceReadonlyScope, StringComparer.Ordinal))
            return Result.Failure<T>("The host's Google grant does not include Meet conference records.", MeetConferenceErrorCodes.MeetScopeMissing);

        var refreshed = false;
        if (McpToolAccessTokenPolicy.IsExpiredOrExpiring(connection) || string.IsNullOrWhiteSpace(connection.EncryptedAccessToken))
        {
            var refresh = await _tokenRefresher.RefreshAccessTokenAsync(plugin!, connection, ct);
            if (!refresh.IsSuccess) return RefreshFailure<T>(refresh);
            refreshed = true;
        }

        var result = await call(Unprotect(connection));
        if (result.Error == MeetRestErrorKind.Unauthorized && !refreshed)
        {
            // The stored expiry can lag reality (clock skew, a grant revoked at Google); the
            // provider's own 401 is the second and last trigger.
            var refresh = await _tokenRefresher.RefreshAccessTokenAsync(plugin!, connection, ct);
            if (!refresh.IsSuccess) return RefreshFailure<T>(refresh);
            result = await call(Unprotect(connection));
        }

        if (result.IsSuccess) return Result.Success(result.Value!);

        _logger.LogDebug("Meet REST call for user {UserId} failed: {Error} {Message}", userId, result.Error, result.Message);
        return result.Error switch
        {
            MeetRestErrorKind.Unauthorized => Result.Failure<T>("Google no longer accepts the host's grant.", MeetConferenceErrorCodes.ConnectionRequired),
            MeetRestErrorKind.ScopeMissing => Result.Failure<T>("The host's Google grant does not include Meet conference records.", MeetConferenceErrorCodes.MeetScopeMissing),
            MeetRestErrorKind.PermissionDenied => Result.Failure<T>("Google refused to show this meeting to the host.", MeetConferenceErrorCodes.PermissionDenied),
            MeetRestErrorKind.NotFound => Result.Failure<T>("Google has no such Meet resource.", MeetConferenceErrorCodes.NotFound),
            MeetRestErrorKind.RateLimited => Result.Failure<T>("Google Meet rate limit reached.", MeetConferenceErrorCodes.ProviderRateLimited),
            MeetRestErrorKind.InvalidRequest => Result.Failure<T>("Invalid Meet resource.", MeetConferenceErrorCodes.InvalidMeeting),
            _ => Result.Failure<T>("Google Meet is unavailable.", MeetConferenceErrorCodes.ProviderUnavailable),
        };
    }

    private string Unprotect(PluginConnection connection) =>
        string.IsNullOrWhiteSpace(connection.EncryptedAccessToken)
            ? string.Empty
            : _credentialProtector.Unprotect(connection.EncryptedAccessToken);

    private Task<Plugin?> GooglePluginAsync(CancellationToken ct) =>
        _unitOfWork.PluginRepository.FirstOrDefaultAsync(
            p => p.Provider == PluginConstants.Providers.Google && p.PluginKey == GoogleMeetPluginKey && p.IsActive,
            ct: ct);

    /// <summary>
    /// <c>null</c> when the user has the google_meet plugin installed AND connected, and it is usable
    /// in <paramref name="workspaceId"/> (the same <see cref="IWorkspacePluginGuard.CanUsePluginInWorkspaceAsync"/>
    /// WarpBot's tool calls pass); a <c>plugin_not_connected</c> failure otherwise. A missing
    /// workspace is refused like any other: there is nothing to judge the plugin by.
    /// </summary>
    private async Task<Result<T>?> CheckPluginConnectedAsync<T>(
        Plugin? plugin, Guid userId, Guid? workspaceId, CancellationToken ct)
    {
        if (plugin is null)
            return PluginNotConnected<T>("The Google Meet plugin is not available.");

        var installation = await _unitOfWork.PluginInstallationRepository.FirstOrDefaultAsync(
            i => i.UserId == userId
                && i.PluginId == plugin.Id
                && i.Status == PluginConstants.InstallationStatus.Installed,
            ct: ct);
        if (installation?.ConnectedAt is null)
            return PluginNotConnected<T>("The host has not connected the Google Meet plugin.");

        // Last, because it asks the workspace service: membership and the workspace's plugin list.
        var usable = await _workspacePlugins.CanUsePluginInWorkspaceAsync(workspaceId, userId, plugin, ct);
        if (!usable.IsSuccess)
        {
            _logger.LogDebug(
                "Google Meet plugin not usable for user {UserId} in workspace {WorkspaceId}: {Error}",
                userId, workspaceId, usable.Error);
            return PluginNotConnected<T>("The Google Meet plugin is not available to the host in this workspace.");
        }

        return null;
    }

    private static Result<T> PluginNotConnected<T>(string message) =>
        Result.Failure<T>(message, MeetConferenceErrorCodes.PluginNotConnected);

    private static Result<T> RefreshFailure<T>(Result refresh) =>
        refresh.ErrorCode switch
        {
            PluginConstants.ErrorCodes.ProviderRateLimited =>
                Result.Failure<T>(refresh.Error ?? "Rate limited.", MeetConferenceErrorCodes.ProviderRateLimited),
            PluginConstants.ErrorCodes.ProviderUnavailable =>
                Result.Failure<T>(refresh.Error ?? "Google is unavailable.", MeetConferenceErrorCodes.ProviderUnavailable),
            _ => Result.Failure<T>(refresh.Error ?? "Reconnect Google.", MeetConferenceErrorCodes.ConnectionRequired),
        };

    private static Result<T> InvalidMeeting<T>() =>
        Result.Failure<T>("Not a Google Meet code or link.", MeetConferenceErrorCodes.InvalidMeeting);
}

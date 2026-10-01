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
    private readonly ILogger<HostMeetConferenceService> _logger;

    public HostMeetConferenceService(
        IUnitOfWork unitOfWork,
        IPluginTokenRefresher tokenRefresher,
        IPluginCredentialProtector credentialProtector,
        IGoogleMeetRestClient meet,
        ILogger<HostMeetConferenceService> logger)
    {
        _unitOfWork = unitOfWork;
        _tokenRefresher = tokenRefresher;
        _credentialProtector = credentialProtector;
        _meet = meet;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<MeetConferenceRecordDto>>> GetConferenceRecordsAsync(
        Guid userId, string meeting, CancellationToken ct = default)
    {
        if (!GoogleMeetCodeParser.TryParse(meeting, out var code))
            return InvalidMeeting<IReadOnlyList<MeetConferenceRecordDto>>();

        return await WithUserTokenAsync(userId, token => _meet.FindConferenceRecordsAsync(token, code, ct), ct);
    }

    public async Task<Result<MeetRosterDto>> GetRosterAsync(Guid userId, string meeting, CancellationToken ct = default)
    {
        if (!GoogleMeetCodeParser.TryParse(meeting, out var code))
            return InvalidMeeting<MeetRosterDto>();

        return await WithUserTokenAsync(userId, async token =>
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
        Guid userId, string meeting, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default)
    {
        if (!GoogleMeetCodeParser.TryParse(meeting, out var code))
            return InvalidMeeting<MeetTranscriptEntriesDto>();

        return await WithUserTokenAsync(userId, async token =>
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
    /// Resolves the user's Google access token, runs <paramref name="call"/> with it, and on a 401
    /// refreshes once and runs it again — the same proactive + reactive refresh WarpBot's tool
    /// calls use, through the same <see cref="IPluginTokenRefresher"/>.
    /// </summary>
    private async Task<Result<T>> WithUserTokenAsync<T>(
        Guid userId,
        Func<string, Task<MeetRestResult<T>>> call,
        CancellationToken ct)
    {
        var connection = await _unitOfWork.PluginConnectionRepository.FirstOrDefaultAsync(
            c => c.UserId == userId && c.Provider == PluginConstants.Providers.Google,
            ct: ct);
        if (connection is null || connection.Status != PluginConstants.ConnectionStatus.Connected)
            return Result.Failure<T>("The host has no connected Google account.", MeetConferenceErrorCodes.ConnectionRequired);

        var scopes = PluginScopeMapper.FromJson(connection.ScopesJson);
        if (!scopes.Contains(MeetConferenceErrorCodes.MeetSpaceReadonlyScope, StringComparer.Ordinal))
            return Result.Failure<T>("The host's Google grant does not include Meet conference records.", MeetConferenceErrorCodes.MeetScopeMissing);

        Plugin? plugin = null;
        var refreshed = false;
        if (McpToolAccessTokenPolicy.IsExpiredOrExpiring(connection) || string.IsNullOrWhiteSpace(connection.EncryptedAccessToken))
        {
            plugin = await GooglePluginAsync(ct);
            if (plugin is null)
                return Result.Failure<T>("No Google plugin is in the catalog.", MeetConferenceErrorCodes.ConnectionRequired);

            var refresh = await _tokenRefresher.RefreshAccessTokenAsync(plugin, connection, ct);
            if (!refresh.IsSuccess) return RefreshFailure<T>(refresh);
            refreshed = true;
        }

        var result = await call(Unprotect(connection));
        if (result.Error == MeetRestErrorKind.Unauthorized && !refreshed)
        {
            // The stored expiry can lag reality (clock skew, a grant revoked at Google); the
            // provider's own 401 is the second and last trigger.
            plugin ??= await GooglePluginAsync(ct);
            if (plugin is null)
                return Result.Failure<T>("No Google plugin is in the catalog.", MeetConferenceErrorCodes.ConnectionRequired);

            var refresh = await _tokenRefresher.RefreshAccessTokenAsync(plugin, connection, ct);
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
            p => p.Provider == PluginConstants.Providers.Google && p.PluginKey == GoogleMeetPluginKey,
            ct: ct);

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

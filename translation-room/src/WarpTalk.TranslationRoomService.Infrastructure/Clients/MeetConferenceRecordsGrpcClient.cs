using System.Globalization;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Clients;

/// <inheritdoc cref="IMeetConferenceRecordsClient"/>
public sealed class MeetConferenceRecordsGrpcClient : IMeetConferenceRecordsClient
{
    private readonly MeetConferenceService.MeetConferenceServiceClient _client;
    private readonly ILogger<MeetConferenceRecordsGrpcClient> _logger;

    public MeetConferenceRecordsGrpcClient(
        MeetConferenceService.MeetConferenceServiceClient client,
        ILogger<MeetConferenceRecordsGrpcClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<MeetConferenceRecordsLookup> GetRecordsAsync(Guid userId, Guid workspaceId, string meetingUrl, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetConferenceRecordsAsync(
                new MeetConferenceLookupRequest
                {
                    UserId = userId.ToString(),
                    Meeting = meetingUrl,
                    WorkspaceId = workspaceId.ToString(),
                },
                cancellationToken: ct);
            if (!string.IsNullOrEmpty(response.ErrorCode))
                return new MeetConferenceRecordsLookup(response.ErrorCode, Array.Empty<MeetConferenceRecordInfo>());

            return new MeetConferenceRecordsLookup(null, response.Records
                .Select(r => new MeetConferenceRecordInfo(r.Name, Parse(r.StartTime), Parse(r.EndTime)))
                .ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A transport failure is "unknown", never "ended": the room stays open.
            _logger.LogDebug(ex, "Meet conference records lookup failed");
            return new MeetConferenceRecordsLookup(MeetConferenceErrorCodes.ProviderUnavailable, Array.Empty<MeetConferenceRecordInfo>());
        }
    }

    private static DateTime? Parse(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;
}

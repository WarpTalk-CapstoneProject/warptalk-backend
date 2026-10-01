using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using GetMeetTranscriptEntriesRequest = WarpTalk.Shared.Protos.GetMeetTranscriptEntriesRequest;
using MeetConferenceServiceClient = WarpTalk.Shared.Protos.MeetConferenceService.MeetConferenceServiceClient;

namespace WarpTalk.TranscriptService.Application.FarSpeakers;

/// <summary>
/// Reads Google Meet's transcript through AssistantService, which holds the host's Google grant.
/// Transport failures become <see cref="MeetConferenceErrorCodes.ProviderUnavailable"/> so the job
/// retries instead of crashing.
/// </summary>
public sealed class MeetTranscriptSource : IMeetTranscriptSource
{
    private readonly MeetConferenceServiceClient _client;
    private readonly ILogger<MeetTranscriptSource> _logger;

    public MeetTranscriptSource(MeetConferenceServiceClient client, ILogger<MeetTranscriptSource> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<MeetTranscriptFetch> GetEntriesAsync(
        Guid hostUserId,
        string meeting,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetMeetTranscriptEntriesAsync(
                new GetMeetTranscriptEntriesRequest
                {
                    UserId = hostUserId.ToString(),
                    Meeting = meeting,
                    WindowStart = windowStartUtc.ToString("O", CultureInfo.InvariantCulture),
                    WindowEnd = windowEndUtc.ToString("O", CultureInfo.InvariantCulture),
                },
                cancellationToken: ct);

            var entries = new List<MeetTranscriptLine>(response.Entries.Count);
            foreach (var entry in response.Entries)
            {
                var start = ParseUnixMs(entry.StartTime);
                var end = ParseUnixMs(entry.EndTime) ?? start;
                if (start is null || end is null) continue;
                entries.Add(new MeetTranscriptLine(entry.ParticipantKey, entry.DisplayName, start.Value, end.Value, entry.Text));
            }

            return new MeetTranscriptFetch(
                string.IsNullOrEmpty(response.ErrorCode) ? null : response.ErrorCode,
                string.IsNullOrEmpty(response.Error) ? null : response.Error,
                response.ConferenceRecords,
                response.TranscriptsReady,
                response.TranscriptsPending,
                response.ConferenceLive,
                entries);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Meet transcript read through assistant-service failed");
            return new MeetTranscriptFetch(
                MeetConferenceErrorCodes.ProviderUnavailable, ex.Message, 0, 0, 0, false, Array.Empty<MeetTranscriptLine>());
        }
    }

    internal static long? ParseUnixMs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : null;
    }
}

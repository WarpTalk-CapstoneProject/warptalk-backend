using System.Collections.Concurrent;

namespace WarpTalk.Gateway.Services;

/// <summary>
/// Thread-safe registry tracking which meetings have active participants.
/// Used by AiResultConsumerService to know which Redis Streams to consume.
/// </summary>
public sealed class ActiveMeetingRegistry
{
    // meetingId → set of speakerIds (userId)
    private readonly ConcurrentDictionary<string, HashSet<string>> _meetings = new();
    private readonly object _lock = new();

    /// <summary>
    /// Fired when the first participant joins a meeting.
    /// AiResultConsumerService subscribes to start consuming streams.
    /// </summary>
    public event Action<string>? MeetingActivated;

    /// <summary>
    /// Fired when the last participant leaves a meeting.
    /// AiResultConsumerService subscribes to stop consuming streams.
    /// </summary>
    public event Action<string>? MeetingDeactivated;

    /// <summary>
    /// Register a participant in a meeting.
    /// Returns true if this is the first participant (meeting just activated).
    /// </summary>
    public bool RegisterParticipant(string meetingId, string speakerId)
    {
        var isFirst = false;

        _meetings.AddOrUpdate(
            meetingId,
            _ =>
            {
                isFirst = true;
                return [speakerId];
            },
            (_, existing) =>
            {
                lock (_lock)
                {
                    existing.Add(speakerId);
                }
                return existing;
            });

        if (isFirst)
        {
            MeetingActivated?.Invoke(meetingId);
        }

        return isFirst;
    }

    /// <summary>
    /// Unregister a participant from a meeting.
    /// Returns true if this was the last participant (meeting deactivated).
    /// </summary>
    public bool UnregisterParticipant(string meetingId, string speakerId)
    {
        if (!_meetings.TryGetValue(meetingId, out var participants))
            return false;

        bool isLast;
        lock (_lock)
        {
            participants.Remove(speakerId);
            isLast = participants.Count == 0;
        }

        if (isLast)
        {
            _meetings.TryRemove(meetingId, out _);
            MeetingDeactivated?.Invoke(meetingId);
        }

        return isLast;
    }

    /// <summary>
    /// Get all currently active meeting IDs.
    /// </summary>
    public IReadOnlyCollection<string> GetActiveMeetingIds()
    {
        return _meetings.Keys.ToList().AsReadOnly();
    }

    /// <summary>
    /// Check if a meeting has active participants.
    /// </summary>
    public bool IsActive(string meetingId) => _meetings.ContainsKey(meetingId);

    public int ActiveMeetingCount => _meetings.Count;
}

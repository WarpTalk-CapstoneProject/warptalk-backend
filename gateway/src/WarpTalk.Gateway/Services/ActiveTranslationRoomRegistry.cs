using System.Collections.Concurrent;

namespace WarpTalk.Gateway.Services;

/// <summary>
/// Thread-safe registry tracking which translationRooms have active participants.
/// Used by AiResultConsumerService to know which Redis Streams to consume.
/// </summary>
public sealed class ActiveTranslationRoomRegistry
{
    // translationRoomId → set of speakerIds (userId)
    private readonly ConcurrentDictionary<string, HashSet<string>> _translationRooms = new();
    private readonly object _lock = new();

    /// <summary>
    /// Fired when the first participant joins a translationRoom.
    /// AiResultConsumerService subscribes to start consuming streams.
    /// </summary>
    public event Action<string>? TranslationRoomActivated;

    /// <summary>
    /// Fired when the last participant leaves a translationRoom.
    /// AiResultConsumerService subscribes to stop consuming streams.
    /// </summary>
    public event Action<string>? TranslationRoomDeactivated;

    /// <summary>
    /// Register a participant in a translationRoom.
    /// Returns true if this is the first participant (translationRoom just activated).
    /// </summary>
    public bool RegisterParticipant(string translationRoomId, string speakerId)
    {
        var isFirst = false;

        _translationRooms.AddOrUpdate(
            translationRoomId,
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
            TranslationRoomActivated?.Invoke(translationRoomId);
        }

        return isFirst;
    }

    /// <summary>
    /// Unregister a participant from a translationRoom.
    /// Returns true if this was the last participant (translationRoom deactivated).
    /// </summary>
    public bool UnregisterParticipant(string translationRoomId, string speakerId)
    {
        if (!_translationRooms.TryGetValue(translationRoomId, out var participants))
            return false;

        bool isLast;
        lock (_lock)
        {
            participants.Remove(speakerId);
            isLast = participants.Count == 0;
        }

        if (isLast)
        {
            _translationRooms.TryRemove(translationRoomId, out _);
            TranslationRoomDeactivated?.Invoke(translationRoomId);
        }

        return isLast;
    }

    /// <summary>
    /// Get all currently active translationRoom IDs.
    /// </summary>
    public IReadOnlyCollection<string> GetActiveTranslationRoomIds()
    {
        return _translationRooms.Keys.ToList().AsReadOnly();
    }

    /// <summary>
    /// Check if a translationRoom has active participants.
    /// </summary>
    public bool IsActive(string translationRoomId) => _translationRooms.ContainsKey(translationRoomId);

    public int ActiveTranslationRoomCount => _translationRooms.Count;
}

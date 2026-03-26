using System.Collections.Concurrent;

namespace WarpTalk.Gateway.Hubs;

/// <summary>
/// Thread-safe in-memory connection manager.
/// For horizontal scaling, this will be supplemented by the Redis backplane
/// (SignalR handles group membership across nodes automatically).
/// </summary>
public sealed class ConnectionManager : IConnectionManager
{
    // userId → set of connectionIds
    private readonly ConcurrentDictionary<string, HashSet<string>> _userConnections = new();

    // connectionId → userId (reverse lookup)
    private readonly ConcurrentDictionary<string, string> _connectionUsers = new();

    private readonly object _lock = new();

    public void AddConnection(string userId, string connectionId)
    {
        _connectionUsers[connectionId] = userId;

        _userConnections.AddOrUpdate(
            userId,
            _ => [connectionId],
            (_, existing) =>
            {
                lock (_lock)
                {
                    existing.Add(connectionId);
                    return existing;
                }
            });
    }

    public bool RemoveConnection(string userId, string connectionId)
    {
        _connectionUsers.TryRemove(connectionId, out _);

        if (!_userConnections.TryGetValue(userId, out var connections))
            return true;

        lock (_lock)
        {
            connections.Remove(connectionId);
            if (connections.Count == 0)
            {
                _userConnections.TryRemove(userId, out _);
                return true; // user fully offline
            }
        }

        return false;
    }

    public IReadOnlyCollection<string> GetConnections(string userId)
    {
        if (!_userConnections.TryGetValue(userId, out var connections))
            return Array.Empty<string>();

        lock (_lock)
        {
            return connections.ToList().AsReadOnly();
        }
    }

    public string? GetUserId(string connectionId)
    {
        _connectionUsers.TryGetValue(connectionId, out var userId);
        return userId;
    }

    public bool IsUserOnline(string userId) => _userConnections.ContainsKey(userId);

    public int OnlineUserCount => _userConnections.Count;
}

namespace WarpTalk.Gateway.Hubs;

/// <summary>
/// Tracks bidirectional userId ↔ connectionId mappings.
/// A single user can have multiple connections (multiple tabs/devices).
/// </summary>
public interface IConnectionManager
{
    /// <summary>Add a connection for a user.</summary>
    void AddConnection(string userId, string connectionId);

    /// <summary>Remove a connection. Returns true if the user has no remaining connections.</summary>
    bool RemoveConnection(string userId, string connectionId);

    /// <summary>Get all connection IDs for a user.</summary>
    IReadOnlyCollection<string> GetConnections(string userId);

    /// <summary>Get the user ID from a connection ID.</summary>
    string? GetUserId(string connectionId);

    /// <summary>Check if a user has any active connections.</summary>
    bool IsUserOnline(string userId);

    /// <summary>Get the count of online users.</summary>
    int OnlineUserCount { get; }
}

using System.Collections.Concurrent;
using WarpTalk.Shared.Coordination;

namespace WarpTalk.Gateway.Tests.Helpers;

/// <summary>An <see cref="IPubSubLeadership"/> whose leadership the test sets directly.</summary>
public sealed class TestPubSubLeadership : IPubSubLeadership
{
    public TestPubSubLeadership(bool isLeader = true)
    {
        ShouldHandle = isLeader;
    }

    public bool ShouldHandle { get; set; }

    public ConcurrentBag<string> Subscribed { get; } = [];

    public void MarkSubscribed(string subscriptionKey) => Subscribed.Add(subscriptionKey);
}

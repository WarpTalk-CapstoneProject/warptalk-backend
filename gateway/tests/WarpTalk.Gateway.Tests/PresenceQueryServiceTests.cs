using Moq;
using WarpTalk.Gateway.Presence;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// The presence snapshot that both <c>NotificationHub.QueryPresence</c> and the legacy
/// <c>POST /api/v1/presence/query</c> serve. These pin the WT-335 privacy contract in the one
/// place it now lives, so neither door can drift from it.
/// </summary>
public class PresenceQueryServiceTests
{
    private const string Caller = "11111111-1111-1111-1111-111111111111";
    private const string Colleague = "22222222-2222-2222-2222-222222222222";
    private const string Stranger = "33333333-3333-3333-3333-333333333333";

    private readonly Mock<IPresenceStore> _store = new();
    private readonly Mock<IPresenceVisibility> _visibility = new();
    private readonly PresenceQueryService _service;

    public PresenceQueryServiceTests()
    {
        _service = new PresenceQueryService(_store.Object, _visibility.Object);

        _store
            .Setup(s => s.GetAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> ids, CancellationToken ct) =>
                ids.ToDictionary(id => id, id => PresenceState.Online));
    }

    private void Visible(params string[] ids) =>
        _visibility
            .Setup(v => v.FilterVisibleAsync(Caller, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public async Task SomeoneOutsideTheCallersWorkspaces_ReadsAsOffline_AndIsNeverLookedUp()
    {
        Visible(Colleague);

        var result = await _service.QueryAsync(Caller, [Colleague, Stranger]);

        Assert.Equal("Online", result.States[Colleague]);
        // Offline, not omitted and not "denied": either of those would confirm the account exists.
        Assert.Equal("Offline", result.States[Stranger]);
        _store.Verify(
            s => s.GetAsync(It.Is<IReadOnlyCollection<string>>(ids => ids.Contains(Stranger)), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AnUnidentifiableCaller_SeesEveryoneOffline_WithoutAMembershipLookup()
    {
        var result = await _service.QueryAsync(null, [Colleague, Stranger]);

        Assert.Equal(2, result.States.Count);
        Assert.All(result.States.Values, state => Assert.Equal("Offline", state));
        _visibility.VerifyNoOtherCalls();
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OneCall_IsCappedAt500Ids()
    {
        var ids = Enumerable.Range(0, PresenceQueryService.MaxUsersPerQuery + 50)
            .Select(_ => Guid.NewGuid().ToString())
            .ToArray();
        IReadOnlyCollection<string>? checkedIds = null;
        _visibility
            .Setup(v => v.FilterVisibleAsync(Caller, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback((string caller, IReadOnlyCollection<string> candidates, CancellationToken ct) => checkedIds = candidates)
            .ReturnsAsync(new HashSet<string>());

        var result = await _service.QueryAsync(Caller, ids);

        Assert.Equal(PresenceQueryService.MaxUsersPerQuery, result.States.Count);
        Assert.Equal(PresenceQueryService.MaxUsersPerQuery, checkedIds!.Count);
    }

    [Fact]
    public async Task BlankAndDuplicateIds_AreDropped_AndTheResponseIsKeyedAsTheCallerSpelledIt()
    {
        var upper = Colleague.ToUpperInvariant();
        // WorkspaceService answers in its own (lower-case) spelling.
        Visible(Colleague);

        var result = await _service.QueryAsync(Caller, [upper, upper, "", "  ", null]);

        var entry = Assert.Single(result.States);
        Assert.Equal(upper, entry.Key);
        Assert.Equal("Online", entry.Value);
    }

    [Fact]
    public async Task VisibilityFailingClosed_ReportsEveryoneOffline()
    {
        Visible();

        var result = await _service.QueryAsync(Caller, [Colleague]);

        Assert.Equal("Offline", result.States[Colleague]);
        _store.VerifyNoOtherCalls();
    }
}

public class PresenceQueryThrottleTests
{
    [Fact]
    public void TheBudgetRefills_OnceTheWindowHasPassed()
    {
        var throttle = new PresenceQueryThrottle();
        var start = new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < PresenceQueryThrottle.MaxCallsPerWindow; i++)
        {
            Assert.True(throttle.TryAcquire(start.AddSeconds(i)));
        }

        Assert.False(throttle.TryAcquire(start + PresenceQueryThrottle.Window - TimeSpan.FromMilliseconds(1)));
        Assert.True(throttle.TryAcquire(start + PresenceQueryThrottle.Window));
    }

    [Fact]
    public void OneConnection_GetsOneThrottle()
    {
        var items = new Dictionary<object, object?>();

        Assert.Same(PresenceQueryThrottle.For(items), PresenceQueryThrottle.For(items));
        Assert.NotSame(PresenceQueryThrottle.For(items), PresenceQueryThrottle.For(new Dictionary<object, object?>()));
    }
}

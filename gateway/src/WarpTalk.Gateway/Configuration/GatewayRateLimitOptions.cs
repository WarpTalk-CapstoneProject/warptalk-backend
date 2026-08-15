namespace WarpTalk.Gateway.Configuration;

public sealed class GatewayRateLimitOptions
{
    public const string SectionName = "RateLimits";

    public int IpPermitLimit { get; init; } = 300;
    public int UserPermitLimit { get; init; } = 180;
    public int WorkspacePermitLimit { get; init; } = 1_000;
    public int LoginPermitLimit { get; init; } = 5;
    public int InboxPermitLimit { get; init; } = 30;

    /// <summary>
    /// Budget for /auth/logout and /auth/refresh, per IP, in its own partition. See
    /// GatewayRateLimiterExtensions.IsSessionRecovery for why these two cannot share the
    /// general anonymous pool.
    ///
    /// Sized for what these endpoints are actually used for rather than for browsing: a
    /// session refreshes about twice an hour and signs out once, so sixty a minute is many
    /// times what a whole office behind one address can legitimately generate, while still
    /// bounding a credential-bearing endpoint.
    /// </summary>
    public int SessionRecoveryPermitLimit { get; init; } = 60;

    public int WindowSeconds { get; init; } = 60;

    public void Validate()
    {
        ValidatePositive(IpPermitLimit, nameof(IpPermitLimit));
        ValidatePositive(UserPermitLimit, nameof(UserPermitLimit));
        ValidatePositive(WorkspacePermitLimit, nameof(WorkspacePermitLimit));
        ValidatePositive(LoginPermitLimit, nameof(LoginPermitLimit));
        ValidatePositive(InboxPermitLimit, nameof(InboxPermitLimit));
        ValidatePositive(SessionRecoveryPermitLimit, nameof(SessionRecoveryPermitLimit));
        ValidatePositive(WindowSeconds, nameof(WindowSeconds));
    }

    private static void ValidatePositive(int value, string name)
    {
        if (value <= 0)
            throw new InvalidOperationException($"{SectionName}:{name} must be greater than zero.");
    }
}

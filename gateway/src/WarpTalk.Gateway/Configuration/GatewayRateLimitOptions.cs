namespace WarpTalk.Gateway.Configuration;

public sealed class GatewayRateLimitOptions
{
    public const string SectionName = "RateLimits";

    public int IpPermitLimit { get; init; } = 300;
    public int UserPermitLimit { get; init; } = 180;
    public int WorkspacePermitLimit { get; init; } = 1_000;
    public int LoginPermitLimit { get; init; } = 5;
    public int InboxPermitLimit { get; init; } = 30;
    public int WindowSeconds { get; init; } = 60;

    public void Validate()
    {
        ValidatePositive(IpPermitLimit, nameof(IpPermitLimit));
        ValidatePositive(UserPermitLimit, nameof(UserPermitLimit));
        ValidatePositive(WorkspacePermitLimit, nameof(WorkspacePermitLimit));
        ValidatePositive(LoginPermitLimit, nameof(LoginPermitLimit));
        ValidatePositive(InboxPermitLimit, nameof(InboxPermitLimit));
        ValidatePositive(WindowSeconds, nameof(WindowSeconds));
    }

    private static void ValidatePositive(int value, string name)
    {
        if (value <= 0)
            throw new InvalidOperationException($"{SectionName}:{name} must be greater than zero.");
    }
}

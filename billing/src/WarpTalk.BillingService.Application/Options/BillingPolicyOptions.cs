namespace WarpTalk.BillingService.Application.Options;

public sealed class BillingPolicyOptions
{
    public const string SectionName = "Billing:Policy";

    public decimal? VatRate { get; init; }
}

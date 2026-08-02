namespace WarpTalk.BillingService.Application.Helpers;

public static class BillingCycleTransactionIdHelper
{
    private const string Prefix = "cycle";

    public static string Create(Guid subscriptionId, DateTime periodEnd)
        => $"{Prefix}-{subscriptionId:N}-{periodEnd:yyyyMMddHHmmss}";
}

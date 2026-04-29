namespace WarpTalk.BillingService.Domain.Enums;

public enum AuditAction
{
    Allocate = 1,
    Deduct = 2,
    Refund = 3,
    Expire = 4,
    TopUp = 5,
    UpgradePlan = 6
}

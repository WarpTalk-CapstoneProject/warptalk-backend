namespace WarpTalk.BillingService.Domain.Enums;

public enum TokenTransactionType
{
    TopUp = 0,
    Consume = 1,
    Adjustment = 2,
    Expire = 3,
    Refund = 4
}

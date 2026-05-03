// =======================================================
// Domain/Enums/LedgerEntryType.cs
// =======================================================

namespace WarpTalk.BillingService.Domain.Enums;

public enum LedgerEntryType
{
    Credit = 1,

    Debit = 2,

    Refund = 3,

    Reservation = 4,

    ReservationRelease = 5,

    Expiration = 6,

    Adjustment = 7
}
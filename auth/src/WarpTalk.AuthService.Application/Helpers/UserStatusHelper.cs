using System;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Enums;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Helpers;

public static class UserStatusHelper
{
    public static AccountStatus GetAccountStatus(User user)
    {
        if (!user.IsActive) return AccountStatus.DISABLED;
        if (user.IsLocked || (user.LockedUntil.HasValue && user.LockedUntil.Value > DateTime.UtcNow)) return AccountStatus.LOCKED;
        if (!user.EmailVerified) return AccountStatus.PENDING;
        return AccountStatus.ACTIVE;
    }

    public static Result<T>? CheckUserStatus<T>(User user)
    {
        var status = GetAccountStatus(user);
        return status switch
        {
            AccountStatus.DISABLED => Result.Failure<T>(AuthConstants.ErrorAccountInactive, ErrorCodes.AccountInactive),
            AccountStatus.LOCKED => Result.Failure<T>(
                user.LockedUntil.HasValue
                    ? string.Format(AuthConstants.ErrorAccountLocked, $"{user.LockedUntil.Value:u}")
                    : AuthConstants.ErrorAccountLockedIndefinitely,
                ErrorCodes.AccountLocked),
            AccountStatus.PENDING => Result.Failure<T>(AuthConstants.ErrorAccountPending, ErrorCodes.AccountPending),
            _ => null
        };
    }
}

using System;

namespace WarpTalk.AuthService.Domain.Constants;

public static class InvitationStatus
{
    public const string Pending = "PENDING";
    public const string Accepted = "ACCEPTED";
    public const string Revoked = "REVOKED";
    public const string Expired = "EXPIRED";
    public const string Replaced = "REPLACED";
}

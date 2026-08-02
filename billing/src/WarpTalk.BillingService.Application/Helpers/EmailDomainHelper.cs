namespace WarpTalk.BillingService.Application.Helpers;

public static class EmailDomainHelper
{
    public static string? NormalizeDomain(string email)
    {
        var atIndex = email.LastIndexOf('@');
        if (atIndex < 0 || atIndex == email.Length - 1)
            return null;

        return email[(atIndex + 1)..].Trim().ToLowerInvariant();
    }
}

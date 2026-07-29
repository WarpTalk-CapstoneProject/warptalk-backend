using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace WarpTalk.AuthService.Domain.ValueObjects;

public record EmailAddress
{
    private static readonly Regex EmailRegex =
        new(Constants.UserConstants.PermittedEmailRegex, RegexOptions.Compiled);

    public string Value { get; }
    public string LocalPart { get; }
    public string Domain { get; }

    public EmailAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !EmailRegex.IsMatch(value))
        {
            throw new ArgumentException("Invalid email format.", nameof(value));
        }

        Value = value.Trim().ToLowerInvariant();
        var parts = Value.Split('@');
        LocalPart = parts[0];
        Domain = parts.Last();
    }

    public string MaskedValue
    {
        get
        {
            if (string.IsNullOrEmpty(LocalPart)) return Value;
            if (LocalPart.Length <= 2) return $"{LocalPart[0]}***@{Domain}";
            return $"{LocalPart.Substring(0, 2)}***@{Domain}";
        }
    }

    public static bool TryParse(string value, out EmailAddress? emailAddress)
    {
        try
        {
            emailAddress = new EmailAddress(value);
            return true;
        }
        catch
        {
            emailAddress = null;
            return false;
        }
    }

    public override string ToString() => Value;
}

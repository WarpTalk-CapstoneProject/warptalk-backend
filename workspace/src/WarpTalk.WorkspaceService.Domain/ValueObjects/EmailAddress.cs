using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace WarpTalk.WorkspaceService.Domain.ValueObjects;

public record EmailAddress
{
    private static readonly HashSet<string> PublicDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "yahoo.com", "outlook.com", "hotmail.com", "icloud.com", 
        "aol.com", "zoho.com", "proton.me", "protonmail.com", "mail.com",
        "live.com", "yandex.com", "gmx.com"
    };

    public bool IsPublicDomain => PublicDomains.Contains(Domain);

    public static bool IsPublicDomainName(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        return PublicDomains.Contains(domain.Trim());
    }

    private static readonly Regex EmailRegex = 
        new(@"^(?i)[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$", RegexOptions.Compiled);

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

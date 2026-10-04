using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WarpTalk.WorkspaceService.Application.Masking;

/// <summary>
/// The structured-PII patterns the security worker masks before it asks the model anything —
/// email, Vietnamese phone, payment card (Luhn-checked), Vietnamese CCCD.
/// </summary>
/// <remarks>
/// A PORT of <c>security_worker/regex_scanners.py</c> in warptalk-ai, kept pattern for pattern.
/// It is not a second scanner: the worker stays the one place that decides what a document
/// contains. This copy answers a narrower question, after the fact and without a network call —
/// "does the masked file we just produced still contain something the worker's own fast path
/// would have hidden?" — so a masked copy that does is never stored.
///
/// If the worker's patterns change, change these with them. Drifting apart fails safe in one
/// direction only (a pattern added here blocks more copies); a pattern added there and not here
/// simply is not double-checked.
/// </remarks>
public static class PiiRegexScanner
{
    private static readonly RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly Regex Email = new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", Options);

    private static readonly Regex VietnamPhone = new(@"(?<!\d)(?:\+84|0)[-. ]?(?:3|5|7|8|9)(?:[-. ]?\d){8}(?!\d)", Options);

    private static readonly Regex VietnamCitizenId = new(
        @"(?<!\d)0(?:\d{11}|\d{2}[-. ]\d{3}[-. ]\d{6}|\d{2}[-. ]\d{3}[-. ]\d{3}[-. ]\d{3})(?!\d)",
        Options);

    private static readonly Regex CardCandidate = new(@"(?<!\d)(?:\d[ -]?){13,19}(?!\d)", Options);

    /// <summary>How many structured-PII matches the text still contains.</summary>
    public static int CountFindings(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var normalized = text.Normalize(NormalizationForm.FormC);
        return Email.Matches(normalized).Count
            + VietnamPhone.Matches(normalized).Count
            + VietnamCitizenId.Matches(normalized).Count
            + CardCandidate.Matches(normalized).Count(m => IsLuhnValid(m.Value));
    }

    private static bool IsLuhnValid(string candidate)
    {
        var digits = candidate.Where(char.IsDigit).Select(c => c - '0').ToArray();
        if (digits.Length < 13 || digits.Length > 19)
        {
            return false;
        }

        var checksum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var digit = digits[digits.Length - 1 - i];
            if (i % 2 == 1)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            checksum += digit;
        }

        return checksum % 10 == 0;
    }
}

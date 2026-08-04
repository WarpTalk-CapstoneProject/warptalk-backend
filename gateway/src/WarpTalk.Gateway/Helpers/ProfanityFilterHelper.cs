using System.Text.RegularExpressions;

namespace WarpTalk.Gateway.Helpers;

public static class ProfanityFilterHelper
{
    private static readonly string ProfanityPattern = @"\b(đụ|đĩ|cặc|lồn|fuck|shit|bitch|asshole|damn|cunt)\b";

    public static string MaskProfanity(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // Simple regex-based profanity masking (for Vietnamese and English common profanity)
        return Regex.Replace(text, ProfanityPattern, "***", RegexOptions.IgnoreCase);
    }
}

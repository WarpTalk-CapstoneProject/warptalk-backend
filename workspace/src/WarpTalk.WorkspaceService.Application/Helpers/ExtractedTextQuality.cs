using System;
using System.Collections.Generic;
using System.Linq;

namespace WarpTalk.WorkspaceService.Application.Helpers;

/// <summary>
/// Is extracted text something a person could read, or the wreckage of a failed extraction?
/// </summary>
/// <remarks>
/// Nothing downstream asks. The chunker cuts by length and the embedder embeds whatever it is
/// given, so a PDF whose text came out as "Td Tj BDC EMC ..." was indexed as-is and answered from.
/// It is also what the PII scan reads, so unreadable text is a document nobody scanned.
///
/// Heuristic, deliberately loose: it only has to separate prose from operator or glyph-code soup,
/// not grade writing. Text too short to judge is let through.
/// </remarks>
public static class ExtractedTextQuality
{
    private const int MinimumCharactersToJudge = 80;
    private const double MinimumLetterRatio = 0.6;
    private const double MaximumOperatorTokenRatio = 0.15;

    private static readonly HashSet<string> PdfOperators = new(StringComparer.Ordinal)
    {
        "BT", "ET", "Td", "TD", "Tj", "TJ", "Tm", "Tf", "T*", "Tc", "Tw", "Tz", "TL", "Tr", "Ts",
        "BDC", "BMC", "EMC", "q", "Q", "cm", "re", "m", "l", "c", "h", "f", "S", "W", "n", "gs", "Do",
    };

    public static bool LooksUnreadable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var visible = text.Count(c => !char.IsWhiteSpace(c));
        if (visible < MinimumCharactersToJudge)
        {
            return false;
        }

        var letters = text.Count(char.IsLetter);
        if ((double)letters / visible < MinimumLetterRatio)
        {
            return true;
        }

        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var operators = tokens.Count(PdfOperators.Contains);
        return tokens.Length > 0 && (double)operators / tokens.Length > MaximumOperatorTokenRatio;
    }
}

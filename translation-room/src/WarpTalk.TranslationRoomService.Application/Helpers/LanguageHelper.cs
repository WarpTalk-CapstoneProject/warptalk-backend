using System;
using System.Collections.Generic;
using System.Text.Json;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

public static class LanguageHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static List<string> ParseTargetLanguages(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();

        var list = JsonSerializer.Deserialize<List<string>>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize TargetLanguages.");

        return list
            .Where(lang => !string.IsNullOrWhiteSpace(lang))
            .Select(lang => NormalizeLanguageCode(lang))
            .ToList();
    }

    public static string SerializeTargetLanguages(List<string>? languages)
    {
        return JsonSerializer.Serialize(languages?.Select(NormalizeLanguageCode).ToList() ?? new List<string>(), JsonOptions);
    }

    /// <summary>
    /// The bare, lower-case primary subtag: `vi-VN`, `vi_VN` and `VI` are all `vi`.
    ///
    /// Splits on BOTH separators. The web (summary-rendering-poll.ts) and the AI side already
    /// read `_` as a separator, and a POSIX-style `vi_VN` reaching here used to survive whole as
    /// `vi_vn` — a "language" no room offers, so the artifact policy refused it and the variant
    /// cache keyed it apart from the `vi` everybody else asked for.
    /// </summary>
    public static string NormalizeLanguageCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        var trimmed = code.Trim();
        var primaryCode = trimmed.Split('-', '_')[0];
        return primaryCode.ToLowerInvariant();
    }
}

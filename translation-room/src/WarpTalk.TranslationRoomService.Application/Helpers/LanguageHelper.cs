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
            .Select(lang => lang.Trim())
            .ToList();
    }

    public static string SerializeTargetLanguages(List<string>? languages)
    {
        return JsonSerializer.Serialize(languages ?? new List<string>(), JsonOptions);
    }
}

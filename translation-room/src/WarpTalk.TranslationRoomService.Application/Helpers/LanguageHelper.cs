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

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static string SerializeTargetLanguages(List<string>? languages)
    {
        return JsonSerializer.Serialize(languages ?? new List<string>(), JsonOptions);
    }
}

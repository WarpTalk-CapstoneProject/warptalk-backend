using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WarpTalk.Shared.PlatformSettings;

/// <summary>
/// Checks a proposed value against its <see cref="SettingDefinition"/>. The server runs this on
/// every write, import and revert; the web runs the same rules for inline feedback, but only the
/// server's answer counts.
/// </summary>
public static class SettingValueValidator
{
    private const int MaxFlagListItems = 500;

    /// <summary>Null when the value is acceptable, otherwise a message the console can show.</summary>
    public static string? Validate(SettingDefinition definition, JsonElement value)
    {
        return definition.Type switch
        {
            SettingValueType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? null
                : "Expected true or false.",
            SettingValueType.Integer => ValidateNumber(definition, value, integral: true),
            SettingValueType.Decimal => ValidateNumber(definition, value, integral: false),
            SettingValueType.String => value.ValueKind == JsonValueKind.String
                ? ValidateString(definition, value.GetString()!, "The value")
                : "Expected text.",
            SettingValueType.Enum => ValidateEnum(definition, value),
            SettingValueType.StringList => ValidateList(definition, value),
            SettingValueType.FeatureFlag => ValidateFlag(value),
            _ => "This setting has an unknown type.",
        };
    }

    private static string? ValidateNumber(SettingDefinition definition, JsonElement value, bool integral)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
            return integral ? "Expected a whole number." : "Expected a number.";
        if (integral && (number != decimal.Truncate(number) || number > long.MaxValue || number < long.MinValue))
            return "Expected a whole number.";
        if (definition.Min is { } min && number < min)
            return $"Must be at least {Format(min)}{UnitSuffix(definition)}.";
        if (definition.Max is { } max && number > max)
            return $"Must be at most {Format(max)}{UnitSuffix(definition)}.";
        return null;
    }

    private static string? ValidateString(SettingDefinition definition, string text, string what)
    {
        if (definition.Type == SettingValueType.String && definition.MaxLength is { } maxLength && text.Length > maxLength)
            return $"{what} must be at most {maxLength} characters.";
        if (text.Length > 2000) return $"{what} is too long.";
        if (text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
            return $"{what} contains control characters.";
        if (definition.Pattern is { } pattern
            && text.Length > 0
            && !Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return $"{what} is not in the expected format.";
        return null;
    }

    private static string? ValidateEnum(SettingDefinition definition, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return "Expected one of the listed options.";
        var text = value.GetString()!;
        return definition.AllowedValues?.Contains(text, StringComparer.Ordinal) == true
            ? null
            : $"'{Truncate(text)}' is not one of: {string.Join(", ", definition.AllowedValues ?? [])}.";
    }

    private static string? ValidateList(SettingDefinition definition, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return "Expected a list.";
        var count = value.GetArrayLength();
        if (definition.MaxLength is { } maxItems && count > maxItems)
            return $"At most {maxItems} entries.";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            index++;
            if (item.ValueKind != JsonValueKind.String) return $"Entry {index} is not text.";
            var text = item.GetString()!;
            if (string.IsNullOrWhiteSpace(text)) return $"Entry {index} is empty.";
            if (definition.AllowedValues is { } allowed && !allowed.Contains(text, StringComparer.Ordinal))
                return $"'{Truncate(text)}' is not one of: {string.Join(", ", allowed)}.";
            if (ValidateString(definition, text, $"Entry {index}") is { } error) return error;
            if (!seen.Add(text)) return $"'{Truncate(text)}' is listed twice.";
        }

        return null;
    }

    private static string? ValidateFlag(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return "Expected a feature flag.";
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "enabled", "rolloutPercent", "allowPlans", "allowWorkspaces", "denyWorkspaces",
        };
        foreach (var property in value.EnumerateObject())
        {
            if (!known.Contains(property.Name)) return $"Unknown flag field '{Truncate(property.Name)}'.";
        }

        if (!value.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return "A flag needs enabled: true or false.";
        if (value.TryGetProperty("rolloutPercent", out var percent)
            && (percent.ValueKind != JsonValueKind.Number || !percent.TryGetInt32(out var p) || p < 0 || p > 100))
            return "Rollout must be a whole percentage from 0 to 100.";

        foreach (var name in new[] { "allowPlans", "allowWorkspaces", "denyWorkspaces" })
        {
            if (!value.TryGetProperty(name, out var list)) continue;
            if (list.ValueKind != JsonValueKind.Array) return $"{name} must be a list.";
            if (list.GetArrayLength() > MaxFlagListItems) return $"{name} has more than {MaxFlagListItems} entries.";
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    return $"{name} may only contain text.";
                if (name != "allowPlans" && !Guid.TryParse(item.GetString(), out _))
                    return $"{name} must list workspace ids.";
                if (name == "allowPlans" && !Regex.IsMatch(item.GetString()!, "^[a-z0-9][a-z0-9_-]{0,49}$", RegexOptions.None, TimeSpan.FromMilliseconds(100)))
                    return "allowPlans must list plan slugs.";
            }
        }

        return null;
    }

    /// <summary>A copy with the same meaning and a stable text form (so equal values compare equal).</summary>
    public static JsonElement Normalize(SettingDefinition definition, JsonElement value)
    {
        if (definition.Type == SettingValueType.FeatureFlag && FeatureFlagValue.TryParse(value) is { } flag)
            return flag.ToJson();
        return JsonSerializer.SerializeToElement(value);
    }

    public static bool AreEqual(JsonElement a, JsonElement b)
        => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    private static string UnitSuffix(SettingDefinition definition)
        => string.IsNullOrEmpty(definition.Unit) ? string.Empty : " " + definition.Unit;

    private static string Format(decimal value) => value.ToString("0.############", CultureInfo.InvariantCulture);

    private static string Truncate(string text) => text.Length > 60 ? text[..60] + "…" : text;
}

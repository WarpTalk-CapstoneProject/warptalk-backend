using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Domain.Constants;

namespace WarpTalk.AssistantService.Application.Helpers;

/// <summary>
/// Checks a submitted tool manifest against the contract WarpBot relies on, before it is written to
/// <c>tools_json</c>.
/// </summary>
/// <remarks>
/// Every rule here exists because breaking it is silent. <c>tools_json</c> is deserialised into
/// <see cref="McpToolDescriptorDto"/> and handed to the model as its tool list; a tool with no
/// label, no effect or a parameters blob that is not a JSON Schema object does not throw anywhere -
/// the model simply stops choosing it, or chooses it and produces arguments the gateway cannot use.
/// The report arrives days later as "the assistant does not do X any more", with nothing pointing
/// back at the manifest edit that caused it.
/// <para>
/// So this validator is deliberately strict and returns every problem at once rather than the first,
/// because an operator editing a manifest by hand wants one round trip, not one per typo.
/// </para>
/// </remarks>
internal static partial class PluginToolManifestValidator
{
    /// <summary>
    /// Tool names travel to the model, come back verbatim in a tool call, and are stored in
    /// <c>plugin_tool_audits.tool_name</c> (VARCHAR(150)). Restricting them to the shape every
    /// existing tool already uses keeps a name from being something a provider's function-calling
    /// schema will reject.
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,150}$")]
    private static partial Regex ToolNamePattern { get; }

    private const int MaxLabelLength = 150;
    private const int MaxDescriptionLength = 1000;
    private const int MaxToolCount = 100;

    /// <summary>
    /// Validates <paramref name="tools"/> and, on success, returns the descriptors to persist with
    /// <paramref name="pluginKey"/> stamped onto every one of them.
    /// </summary>
    /// <returns>
    /// <c>Errors</c> empty and <c>Tools</c> populated when the manifest is valid; otherwise
    /// <c>Errors</c> holds every failure found and <c>Tools</c> is empty.
    /// </returns>
    public static (IReadOnlyList<string> Errors, IReadOnlyList<McpToolDescriptorDto> Tools) Validate(
        string pluginKey,
        IReadOnlyList<PluginToolManifestEntryDto>? tools)
    {
        var errors = new List<string>();

        // A body with no "tools" property at all is almost certainly a mistake rather than an
        // intent to clear the manifest. Clearing is spelled "tools": [], which cannot be typed by
        // accident.
        if (tools is null)
        {
            errors.Add("A 'tools' array is required. Send an empty array to clear the manifest.");
            return (errors, []);
        }

        if (tools.Count > MaxToolCount)
            errors.Add($"A manifest may hold at most {MaxToolCount} tools; {tools.Count} were sent.");

        var descriptors = new List<McpToolDescriptorDto>(tools.Count);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < tools.Count; index++)
        {
            var tool = tools[index];
            var position = $"tools[{index}]";

            if (tool is null)
            {
                errors.Add($"{position} is null.");
                continue;
            }

            var name = tool.Name?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                errors.Add($"{position}: 'name' is required.");
            }
            else if (!ToolNamePattern.IsMatch(name))
            {
                errors.Add(
                    $"{position}: 'name' must be 1-150 characters of letters, digits, '_', '.' or '-' (got '{name}').");
            }
            // Case-insensitive, though the orchestrator resolves a tool call with
            // StringComparison.Ordinal: two names differing only in case would resolve correctly
            // and still leave the model choosing between two entries it cannot tell apart.
            else if (!seenNames.Add(name))
            {
                errors.Add($"{position}: duplicate tool name '{name}'.");
            }

            var label = tool.Label?.Trim() ?? string.Empty;
            if (label.Length == 0)
                errors.Add($"{position}: 'label' is required - it is what the UI shows for the tool.");
            else if (label.Length > MaxLabelLength)
                errors.Add($"{position}: 'label' must be at most {MaxLabelLength} characters.");

            var description = tool.Description?.Trim() ?? string.Empty;
            if (description.Length == 0)
            {
                errors.Add(
                    $"{position}: 'description' is required - it is the only thing telling the model when to pick this tool.");
            }
            else if (description.Length > MaxDescriptionLength)
            {
                errors.Add($"{position}: 'description' must be at most {MaxDescriptionLength} characters.");
            }

            var effect = tool.Effect?.Trim() ?? string.Empty;
            // The effect is not decoration: 'write' is what makes a call require a confirmation
            // token before it runs. A tool that should be 'write' and says 'read' executes a side
            // effect with no confirmation at all.
            if (effect.Length == 0)
            {
                errors.Add($"{position}: 'effect' is required.");
            }
            else if (effect is not (PluginConstants.ToolEffect.Read or PluginConstants.ToolEffect.Write))
            {
                errors.Add(
                    $"{position}: 'effect' must be '{PluginConstants.ToolEffect.Read}' or "
                    + $"'{PluginConstants.ToolEffect.Write}' (got '{effect}').");
            }

            var scopes = ValidateScopes(tool.RequiredScopes, position, errors);
            var parameters = ValidateParameters(tool.Parameters, position, errors);

            descriptors.Add(new McpToolDescriptorDto(
                name,
                // Stamped from the route, never taken from the body: the orchestrator resolves a
                // tool call by this key, so a manifest carrying another row's key would send calls
                // to the wrong plugin.
                pluginKey,
                label,
                description,
                effect,
                scopes,
                parameters ?? new JsonObject()));
        }

        return errors.Count > 0 ? (errors, []) : (errors, descriptors);
    }

    private static IReadOnlyList<string> ValidateScopes(
        IReadOnlyList<string>? requiredScopes,
        string position,
        List<string> errors)
    {
        // Required as a property, allowed to be empty as a value. An MCP server can genuinely
        // expose a tool that needs no incremental scope; a manifest that simply forgot the property
        // is a different thing, and one the scope check at call time would read as "needs nothing".
        if (requiredScopes is null)
        {
            errors.Add(
                $"{position}: 'requiredScopes' is required. Use [] for a tool that needs no additional scope.");
            return [];
        }

        var scopes = new List<string>(requiredScopes.Count);
        foreach (var scope in requiredScopes)
        {
            var trimmed = scope?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
            {
                errors.Add($"{position}: 'requiredScopes' contains a blank entry.");
                continue;
            }

            if (scopes.Contains(trimmed, StringComparer.Ordinal))
            {
                errors.Add($"{position}: 'requiredScopes' repeats '{trimmed}'.");
                continue;
            }

            scopes.Add(trimmed);
        }

        return scopes;
    }

    /// <summary>
    /// Checks that <c>parameters</c> is a JSON Schema object of the shape a function-calling API
    /// accepts, which is narrower than "is valid JSON".
    /// </summary>
    private static JsonObject? ValidateParameters(JsonObject? parameters, string position, List<string> errors)
    {
        if (parameters is null)
        {
            errors.Add($"{position}: 'parameters' is required and must be a JSON Schema object.");
            return null;
        }

        var type = AsString(parameters["type"]);
        if (!string.Equals(type, "object", StringComparison.Ordinal))
        {
            errors.Add($"{position}: 'parameters.type' must be \"object\" (got {Describe(parameters["type"])}).");
            return parameters;
        }

        if (parameters["properties"] is not JsonObject properties)
        {
            errors.Add(
                $"{position}: 'parameters.properties' must be an object. Use {{}} for a tool that takes no arguments.");
            return parameters;
        }

        foreach (var property in properties)
        {
            if (property.Value is not JsonObject)
                errors.Add($"{position}: 'parameters.properties.{property.Key}' must be an object.");
        }

        if (parameters["required"] is { } requiredNode)
        {
            if (requiredNode is not JsonArray requiredArray)
            {
                errors.Add($"{position}: 'parameters.required' must be an array of property names.");
            }
            else
            {
                foreach (var entry in requiredArray)
                {
                    var propertyName = AsString(entry);
                    if (string.IsNullOrWhiteSpace(propertyName))
                    {
                        errors.Add($"{position}: 'parameters.required' contains a blank entry.");
                    }
                    // A required name that is not declared in 'properties' is the manifest bug that
                    // costs the most to find: the model is told an argument is mandatory and given
                    // no schema for it, so it invents one and the gateway rejects the call.
                    else if (!properties.ContainsKey(propertyName))
                    {
                        errors.Add(
                            $"{position}: 'parameters.required' names '{propertyName}', which is not declared in 'properties'.");
                    }
                }
            }
        }

        return parameters;
    }

    /// <summary>
    /// Reads a node as a string, or null when it is absent or is any other JSON type.
    /// <c>JsonNode.GetValue&lt;string&gt;()</c> throws on a number or a boolean, and a manifest
    /// holding <c>"type": 3</c> has to come back as a validation error rather than an exception.
    /// </summary>
    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Describe(JsonNode? node) => node is null ? "nothing" : $"\"{node}\"";
}

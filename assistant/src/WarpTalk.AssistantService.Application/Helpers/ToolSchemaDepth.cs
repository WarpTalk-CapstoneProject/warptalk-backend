using System.Text.Json.Nodes;

namespace WarpTalk.AssistantService.Application.Helpers;

/// <summary>
/// Bounds how deeply a tool's parameter schema nests before anything else sees it.
/// </summary>
/// <remarks>
/// <para>
/// 3 Oct 2026: Notion's MCP server synced 44 tools whose <c>notion-query-data-sources</c> input
/// schema nests 32 levels (recursive compound filters). The catalog response wraps every tool's
/// schema five levels down, and ASP.NET Core serialises with <c>MaxDepth = 32</c>, so
/// <c>GET /api/v1/assistant/plugins</c> threw half-way through writing a 200: every user's Plugins
/// page read "Could not load plugins", and <c>GET /mcp/tools</c> - what WarpBot reads - carried the
/// same schema, where a broken body makes the worker drop every plugin tool, not only Notion's.
/// </para>
/// <para>
/// Applied where every reader comes through (<c>PluginDefinitionMapper</c>), so a manifest already
/// stored deep is fixed without rewriting it. Below the limit a schema is returned unchanged (Linear,
/// the deepest before Notion, is 8). At the limit nested containers are dropped and scalar members
/// (<c>type</c>, <c>description</c>, <c>format</c>…) kept, so a trimmed branch still says what it is
/// and only becomes looser: the model loses guidance deep inside a filter, and the MCP server still
/// validates whatever arrives.
/// </para>
/// </remarks>
public static class ToolSchemaDepth
{
    /// <summary>Container levels a parameter schema may use, counting its own root object.</summary>
    public const int MaxDepth = 12;

    public static JsonObject Clamp(JsonObject? schema, int maxDepth = MaxDepth)
    {
        if (schema is null) return new JsonObject();
        if (DepthOf(schema) <= maxDepth) return schema;
        return CloneWithin(schema, maxDepth) as JsonObject ?? new JsonObject();
    }

    /// <summary>Container nesting of <paramref name="node"/>: a scalar is 0, <c>{}</c> is 1.</summary>
    public static int DepthOf(JsonNode? node) => node switch
    {
        JsonObject obj => 1 + obj.Select(member => DepthOf(member.Value)).DefaultIfEmpty(0).Max(),
        JsonArray array => 1 + array.Select(DepthOf).DefaultIfEmpty(0).Max(),
        _ => 0,
    };

    /// <summary>A copy using at most <paramref name="budget"/> container levels, or null to drop it.</summary>
    private static JsonNode? CloneWithin(JsonNode? node, int budget)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                if (budget <= 0) return null;
                var copy = new JsonObject();
                foreach (var (name, value) in obj)
                {
                    if (value is null)
                    {
                        copy[name] = null;
                        continue;
                    }
                    var child = CloneWithin(value, budget - 1);
                    if (child is not null) copy[name] = child;
                }
                return copy;
            }
            case JsonArray array:
            {
                if (budget <= 0) return null;
                var copy = new JsonArray();
                foreach (var element in array)
                {
                    var child = element is null ? null : CloneWithin(element, budget - 1);
                    if (child is not null || element is null) copy.Add(child);
                }
                // An anyOf/oneOf/items list whose every branch was too deep is not an empty list
                // of choices - `anyOf: []` matches nothing. Drop it, so the parent just loosens.
                return copy.Count == 0 && array.Count > 0 ? null : copy;
            }
            default:
                return node?.DeepClone();
        }
    }
}

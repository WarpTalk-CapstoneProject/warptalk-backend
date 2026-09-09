using System.Text.Json.Nodes;

namespace WarpTalk.AssistantService.Application.Helpers;

internal static class JsonCanonicalizer
{
    public static string ToCanonicalString(JsonNode? node)
    {
        return node switch
        {
            null => "null",
            JsonObject obj => "{" + string.Join(",", obj
                .OrderBy(property => property.Key, StringComparer.Ordinal)
                .Select(property => $"{JsonValue.Create(property.Key)!.ToJsonString()}:{ToCanonicalString(property.Value)}")) + "}",
            JsonArray array => "[" + string.Join(",", array.Select(ToCanonicalString)) + "]",
            _ => node.ToJsonString(),
        };
    }
}

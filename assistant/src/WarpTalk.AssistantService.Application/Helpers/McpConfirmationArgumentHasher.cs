using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace WarpTalk.AssistantService.Application.Helpers;

internal static class McpConfirmationArgumentHasher
{
    public static string Hash(JsonObject? arguments)
    {
        var canonical = JsonCanonicalizer.ToCanonicalString(arguments);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

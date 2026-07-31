using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WarpTalk.WorkspaceService.Application.Helpers;

public sealed record RolePreviewTokenPayload(
    Guid WorkspaceId,
    Guid TargetUserId,
    string OldRole,
    string NewRole,
    long ExpiresAtUnix,
    long CoolingOffUntilUnix);

public static class RolePreviewTokenHelper
{
    public static string CreatePreviewToken(
        Guid workspaceId,
        Guid targetUserId,
        string oldRole,
        string newRole,
        long expiresAtUnix,
        long coolingOffUntilUnix,
        byte[] signingKey)
    {
        var payload = new RolePreviewTokenPayload(workspaceId, targetUserId, oldRole, newRole, expiresAtUnix, coolingOffUntilUnix);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var encodedPayload = Base64UrlEncode(payloadBytes);
        using var hmac = new HMACSHA256(signingKey);
        var signature = Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(encodedPayload)));
        return $"{encodedPayload}.{signature}";
    }

    public static bool TryReadPreviewToken(
        string token,
        byte[] signingKey,
        out RolePreviewTokenPayload payload)
    {
        payload = default!;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('.', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        try
        {
            using var hmac = new HMACSHA256(signingKey);
            var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(parts[0]));
            var actual = Base64UrlDecode(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return false;
            var json = Base64UrlDecode(parts[0]);
            payload = JsonSerializer.Deserialize<RolePreviewTokenPayload>(json)!;
            return payload != null;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}

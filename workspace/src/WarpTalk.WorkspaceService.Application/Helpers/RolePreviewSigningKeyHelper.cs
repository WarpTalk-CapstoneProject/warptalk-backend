using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace WarpTalk.WorkspaceService.Application.Helpers;

public static class RolePreviewSigningKeyHelper
{
    public static bool TryResolve(IConfiguration? configuration, out byte[] signingKey)
    {
        var configuredKey = new[]
            {
                configuration?["Security:RolePreviewSigningKey"],
                Environment.GetEnvironmentVariable("WARPTALK_ROLE_PREVIEW_SIGNING_KEY"),
                configuration?["Jwt:Secret"]
            }
            .FirstOrDefault(IsUsable);

        if (configuredKey == null)
        {
            signingKey = [];
            return false;
        }

        signingKey = SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));
        return true;
    }

    public static bool IsUsable(string? configuredKey)
    {
        return !string.IsNullOrWhiteSpace(configuredKey)
            && !configuredKey.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
            && !configuredKey.Contains("placeholder", StringComparison.OrdinalIgnoreCase);
    }
}

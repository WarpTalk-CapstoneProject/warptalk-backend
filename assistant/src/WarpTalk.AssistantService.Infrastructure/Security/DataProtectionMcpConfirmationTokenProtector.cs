using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Infrastructure.Security;

public class DataProtectionMcpConfirmationTokenProtector : IMcpConfirmationTokenProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionMcpConfirmationTokenProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("WarpTalk.AssistantService.McpConfirmationTokens.v1");
    }

    public string Protect(McpConfirmationTokenPayloadDto payload)
    {
        return _protector.Protect(JsonSerializer.Serialize(payload));
    }

    public Result<McpConfirmationTokenPayloadDto> Unprotect(string token)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<McpConfirmationTokenPayloadDto>(_protector.Unprotect(token));
            return payload == null
                ? Result.Failure<McpConfirmationTokenPayloadDto>("Confirmation token payload is empty.", PluginConstants.ErrorCodes.PermissionDenied)
                : Result.Success(payload);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return Result.Failure<McpConfirmationTokenPayloadDto>("Confirmation token is invalid.", PluginConstants.ErrorCodes.PermissionDenied);
        }
    }
}

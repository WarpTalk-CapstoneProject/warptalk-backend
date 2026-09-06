using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;

namespace WarpTalk.AssistantService.Infrastructure.Security;

public class DataProtectionPluginOAuthStateProtector : IPluginOAuthStateProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionPluginOAuthStateProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("WarpTalk.AssistantService.PluginOAuthState.v1");
    }

    public string Protect(PluginOAuthStateDto state)
    {
        return _protector.Protect(JsonSerializer.Serialize(state));
    }

    public PluginOAuthStateDto Unprotect(string value)
    {
        return JsonSerializer.Deserialize<PluginOAuthStateDto>(_protector.Unprotect(value))
            ?? throw new InvalidOperationException("OAuth state payload is empty.");
    }
}

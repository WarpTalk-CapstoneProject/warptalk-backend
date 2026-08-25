using Microsoft.AspNetCore.DataProtection;
using WarpTalk.AssistantService.Application.Interfaces;

namespace WarpTalk.AssistantService.Infrastructure.Security;

public class DataProtectionPluginCredentialProtector : IPluginCredentialProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionPluginCredentialProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("WarpTalk.AssistantService.PluginCredentials.v1");
    }

    public string Protect(string value) => _protector.Protect(value);

    public string Unprotect(string value) => _protector.Unprotect(value);
}

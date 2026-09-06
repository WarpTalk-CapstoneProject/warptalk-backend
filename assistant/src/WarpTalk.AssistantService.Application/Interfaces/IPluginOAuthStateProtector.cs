using WarpTalk.AssistantService.Application.DTOs;

namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IPluginOAuthStateProtector
{
    string Protect(PluginOAuthStateDto state);

    PluginOAuthStateDto Unprotect(string value);
}

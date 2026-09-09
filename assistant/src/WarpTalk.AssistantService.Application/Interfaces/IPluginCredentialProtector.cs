namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IPluginCredentialProtector
{
    string Protect(string value);
    string Unprotect(string value);
}

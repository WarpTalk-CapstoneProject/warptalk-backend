namespace WarpTalk.AssistantService.Domain.Exceptions;

/// <summary>
/// A plugin's server answered in a way that has a name: refused the credentials, lacked a scope,
/// rate limited, unreachable. Carries one of <c>PluginConstants.ErrorCodes</c>.
/// </summary>
/// <remarks>
/// Public, and in Domain, so Application can act on the reason without knowing HTTP exists. The
/// API-key connect needs exactly that: a 401 means the pasted key is wrong and the user should be
/// told so, while an outage means try again - and before this the reason was a private type inside
/// the gateway that nothing outside it could catch.
/// </remarks>
public class PluginProviderException : Exception
{
    public PluginProviderException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

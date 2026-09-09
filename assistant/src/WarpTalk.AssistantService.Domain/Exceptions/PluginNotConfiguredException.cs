namespace WarpTalk.AssistantService.Domain.Exceptions;

/// <summary>
/// WarpTalk itself is not configured to talk to this provider: an empty client id or secret, a
/// catalog row with no token endpoint. Not the provider's fault, and not the user's.
/// </summary>
/// <remarks>
/// It exists to be distinguishable. Every guard of this kind used to throw a plain
/// <see cref="InvalidOperationException"/>, which is also what the OAuth clients throw when a
/// provider refuses an exchange - so the callback could not tell "our client secret is missing"
/// from "Google said no", and the two need opposite words. One is worth telling the user to try
/// again about; the other will fail identically forever until an operator sets a variable.
/// <para>
/// Derived from <see cref="InvalidOperationException"/> rather than from <see cref="Exception"/>,
/// so any existing <c>catch (InvalidOperationException)</c> keeps behaving exactly as it did.
/// </para>
/// </remarks>
public class PluginNotConfiguredException : InvalidOperationException
{
    public PluginNotConfiguredException(string message) : base(message)
    {
    }
}

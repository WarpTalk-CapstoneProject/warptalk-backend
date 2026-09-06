using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// Picks the gateway and OAuth client that serve a given plugin, keyed on the plugin's
/// <see cref="Plugin.Kind"/>.
/// </summary>
/// <remarks>
/// Before this existed, <c>IMcpToolGateway</c> and <c>IPluginOAuthClient</c> each had exactly one
/// registered implementation and both of them opened by throwing unless the plugin key was
/// <c>google_workspace</c>. That made "add an app" mean "write two classes and deploy". Dispatching
/// on kind instead of key means a real MCP server needs no code at all - one catalog row is enough -
/// while a provider that genuinely needs bespoke handling (Google Drive/Calendar has no official
/// remote MCP server) keeps its own implementation alongside.
/// <para>
/// Resolution failing is a programming error, not an expected outcome: a row's kind is constrained
/// by the database, so an unresolvable kind means the catalog and the code have drifted apart.
/// </para>
/// </remarks>
public interface IPluginProviderResolver
{
    IMcpToolGateway ResolveGateway(string pluginKind);

    IPluginOAuthClient ResolveOAuthClient(string pluginKind);
}

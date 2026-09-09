using WarpTalk.AssistantService.Application.DTOs;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// The asymmetric keys WarpTalk uses to authenticate as a <c>private_key_jwt</c> client, and the
/// public half it publishes at <c>jwks_uri</c>.
/// </summary>
/// <remarks>
/// A metadata-document client cannot hold a shared secret - the document is public - so an
/// asymmetric key is the only way to stay a confidential client at servers that accept one.
/// <para>
/// Two properties are load-bearing and easy to get wrong. The private half must <em>survive
/// container restart and be shared across replicas</em>: a per-instance key means every redeploy
/// silently invalidates client authentication at every server. And more than one key must be
/// publishable at once, so a rotation overlaps rather than cutting over - a server caching our
/// JWKS must still find the old <c>kid</c> while in-flight assertions drain.
/// </para>
/// <para>
/// <see cref="HasSigningKey"/> false is a supported state, not a failure: without keys the client
/// simply does not advertise <c>private_key_jwt</c> and negotiates <c>none</c> instead. What must
/// never happen is advertising a capability the JWKS cannot back.
/// </para>
/// </remarks>
public interface IMcpClientSigningKeyStore
{
    bool HasSigningKey { get; }

    /// <summary>The key new assertions are signed with. Null when <see cref="HasSigningKey"/> is false.</summary>
    McpSigningKeyDto? ActiveKey { get; }

    /// <summary>
    /// Every key that should still be trusted, active first. Published verbatim as the JWKS, so a
    /// key removed from here stops being accepted only once caches expire.
    /// </summary>
    IReadOnlyList<McpPublicJsonWebKeyDto> PublishedKeys { get; }
}

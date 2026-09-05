using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AssistantService.Application.Interfaces;

namespace WarpTalk.AssistantService.API.Controllers;

/// <summary>
/// Publishes WarpTalk's Client ID Metadata Document and the JWKS it points at.
/// </summary>
/// <remarks>
/// These are fetched server-to-server by authorization servers we are trying to authenticate
/// against, so they are necessarily anonymous - the fetcher has no WarpTalk credentials and never
/// will. Nothing secret is exposed: the document is public by design and the JWKS carries public
/// key parameters only.
/// <para>
/// Both must be reachable on the deployment's public host, not just on the service port. The
/// Client Identifier URL a provider is handed has to resolve from the open internet, and getting
/// this wrong is the same class of mistake as T044's 5108-vs-5200 redirect drift, only harder to
/// spot because the failure surfaces as a rejected authorization on the provider's side.
/// </para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("oauth/client-metadata")]
public class McpClientMetadataController : ControllerBase
{
    /// <summary>
    /// Short enough that an edit propagates in minutes rather than the week a server is allowed to
    /// cache for, long enough that a busy provider is not refetching on every authorization.
    /// </summary>
    private const int CacheSeconds = 300;

    private readonly IMcpClientMetadataProvider _metadataProvider;

    public McpClientMetadataController(IMcpClientMetadataProvider metadataProvider)
    {
        _metadataProvider = metadataProvider;
    }

    /// <remarks>
    /// The <c>v1</c> in the path is load-bearing. A breaking change - a new redirect URI, a
    /// different auth method - must ship as <c>v2.json</c>, a new client identity, because
    /// in-flight authorizations and cached copies are still validating against the old document.
    /// </remarks>
    [HttpGet("v1.json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetClientMetadataDocument()
    {
        var document = _metadataProvider.BuildClientMetadataDocument();

        // 404 rather than an empty document when the CIMD rung is not configured: a malformed
        // document would be fetched, parsed, and rejected by a provider, which is a worse and much
        // more confusing failure than the URL simply not resolving.
        if (document is null) return NotFound();

        Response.Headers.CacheControl = $"public, max-age={CacheSeconds}";
        return Ok(document);
    }

    [HttpGet("jwks.json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetJwks()
    {
        Response.Headers.CacheControl = $"public, max-age={CacheSeconds}";
        return Ok(_metadataProvider.BuildJwks());
    }
}

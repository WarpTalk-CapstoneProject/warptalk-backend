using WarpTalk.AssistantService.Application.DTOs;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// Builds the two documents WarpTalk publishes so authorization servers can identify it: the
/// Client ID Metadata Document, and the JWKS it points at.
/// </summary>
/// <remarks>
/// Both are derived from live configuration rather than checked in as static files, for one
/// reason: the document must never advertise a capability the process cannot back. With no signing
/// keys loaded, <c>private_key_jwt</c> and <c>jwks_uri</c> must be absent - a server that reads
/// them and then cannot verify an assertion fails the flow in a way nobody can diagnose from the
/// outside.
/// </remarks>
public interface IMcpClientMetadataProvider
{
    /// <summary>Null when no client metadata URL is configured, which disables the CIMD rung.</summary>
    McpClientMetadataDocumentDto? BuildClientMetadataDocument();

    McpJsonWebKeySetDto BuildJwks();
}

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;

namespace WarpTalk.AssistantService.Infrastructure.Mcp;

/// <summary>
/// Loads ES256 signing keys from configuration, so the private material lives wherever the
/// deployment already keeps its secrets rather than in a per-container key ring.
/// </summary>
/// <remarks>
/// Configuration is the deliberate choice, and it is the fix for the failure mode T042 hit with
/// Data Protection: a key generated at startup differs in every replica and is gone after every
/// restart, which for client authentication means every MCP connection breaks on redeploy with no
/// obvious cause. A key supplied from outside the process has neither problem.
/// <para>
/// A malformed key is dropped with a warning rather than taking the service down. The client then
/// degrades to <c>none</c> at servers that allow it, which is a working plugin; throwing here
/// would be an outage across every plugin, including the ones that never needed a key.
/// </para>
/// <para>
/// JWT work goes through <c>Microsoft.IdentityModel.JsonWebTokens</c> rather than being assembled
/// by hand. Signing alone would be safe to hand-roll - the dangerous half of JWT is verification -
/// but this service also validates the <c>id_token</c> an authorization server returns during
/// connect, which is what lets a user's provider identity come out of the token they just consented
/// to instead of costing them another sign-in round trip. Validation must never be hand-rolled, and
/// running one library for both directions beats running two mechanisms.
/// </para>
/// </remarks>
public class ConfigurationMcpClientSigningKeyStore : IMcpClientSigningKeyStore, IDisposable
{
    private const string Algorithm = "ES256";

    private readonly List<LoadedKey> _keys = [];

    public ConfigurationMcpClientSigningKeyStore(
        IOptions<McpClientOptions> options,
        ILogger<ConfigurationMcpClientSigningKeyStore> logger)
    {
        foreach (var configured in options.Value.SigningKeys)
        {
            if (string.IsNullOrWhiteSpace(configured.Kid) || string.IsNullOrWhiteSpace(configured.PrivateKeyPem))
            {
                logger.LogWarning("Skipping an MCP client signing key with no kid or no private key material.");
                continue;
            }

            try
            {
                var ecdsa = ECDsa.Create();
                ecdsa.ImportFromPem(configured.PrivateKeyPem);

                if (ecdsa.KeySize != 256)
                {
                    logger.LogWarning(
                        "Skipping MCP client signing key {Kid}: ES256 requires a P-256 key, got {KeySize}-bit.",
                        configured.Kid,
                        ecdsa.KeySize);
                    ecdsa.Dispose();
                    continue;
                }

                _keys.Add(new LoadedKey(configured.Kid, ecdsa));
            }
            catch (Exception e) when (e is CryptographicException or ArgumentException)
            {
                logger.LogWarning(
                    e,
                    "Skipping MCP client signing key {Kid}: the private key could not be read.",
                    configured.Kid);
            }
        }

        if (_keys.Count == 0)
        {
            logger.LogInformation(
                "No MCP client signing keys are configured; the client will negotiate "
                    + "token_endpoint_auth_method=none and will not advertise private_key_jwt.");
        }
    }

    public bool HasSigningKey => _keys.Count > 0;

    /// <summary>First configured key signs; the rest stay published so a rotation can overlap.</summary>
    public McpSigningKeyDto? ActiveKey =>
        _keys.Count == 0 ? null : new McpSigningKeyDto(_keys[0].Kid, Algorithm);

    public IReadOnlyList<McpPublicJsonWebKeyDto> PublishedKeys =>
        _keys.Select(ToPublicJwk).ToArray();

    /// <summary>
    /// Produces a signed <c>private_key_jwt</c> client assertion. Infrastructure-only by design:
    /// the private material must not be reachable from Application.
    /// </summary>
    /// <param name="clientId">Both <c>iss</c> and <c>sub</c>, per RFC 7523.</param>
    /// <param name="tokenEndpoint">The <c>aud</c>: the endpoint the assertion may be spent at.</param>
    public string? CreateClientAssertion(string clientId, string tokenEndpoint, TimeSpan lifetime)
    {
        if (_keys.Count == 0) return null;

        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = clientId,
            Audience = tokenEndpoint,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(lifetime),
            Subject = new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim(JwtRegisteredClaimNames.Sub, clientId),
                // A replay window of one is what jti buys: servers reject a repeated identifier, so
                // a captured assertion cannot be spent twice inside its short lifetime.
                new System.Security.Claims.Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
            SigningCredentials = CreateSigningCredentials(),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>The active key, wrapped for signing. Its <c>kid</c> reaches the assertion header.</summary>
    private SigningCredentials CreateSigningCredentials()
    {
        var key = _keys[0];
        return new SigningCredentials(
            new ECDsaSecurityKey(key.Key) { KeyId = key.Kid },
            SecurityAlgorithms.EcdsaSha256);
    }

    private static McpPublicJsonWebKeyDto ToPublicJwk(LoadedKey key)
    {
        // Converted rather than assembled by hand so the curve name and point encoding come from
        // the same library a server will use to read them back.
        var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(
            new ECDsaSecurityKey(key.Key) { KeyId = key.Kid });

        return new McpPublicJsonWebKeyDto(
            Kty: jwk.Kty,
            Crv: jwk.Crv,
            X: jwk.X,
            Y: jwk.Y,
            Kid: key.Kid,
            Alg: Algorithm,
            Use: "sig");
    }

    public void Dispose()
    {
        foreach (var key in _keys) key.Key.Dispose();
        _keys.Clear();
        GC.SuppressFinalize(this);
    }

    private sealed record LoadedKey(string Kid, ECDsa Key);
}

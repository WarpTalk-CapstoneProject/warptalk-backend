using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WarpTalk.AssistantService.Infrastructure.Mcp;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// The client assertion is assembled by hand rather than by an identity library, so these tests
/// are the mitigation for that choice: they verify the produced JWS against the published public
/// key exactly as an authorization server would.
/// </summary>
/// <remarks>
/// The signature-format assertion is the one that matters most. ES256 in JWS is a fixed-width
/// r-then-s concatenation; emitting the DER encoding <see cref="ECDsa"/> defaults to elsewhere is
/// the classic way a hand-rolled signer produces something every server rejects, and nothing but a
/// real verification catches it.
/// </remarks>
public class McpClientSigningKeyStoreTests
{
    private static (string Pem, ECDsa Key) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key);
    }

    private static ConfigurationMcpClientSigningKeyStore StoreWith(params (string Kid, string Pem)[] keys) =>
        new(
            Options.Create(new McpClientOptions
            {
                SigningKeys = keys
                    .Select(k => new McpClientSigningKeyOptions { Kid = k.Kid, PrivateKeyPem = k.Pem })
                    .ToList(),
            }),
            NullLogger<ConfigurationMcpClientSigningKeyStore>.Instance);

    [Fact]
    public void HasSigningKey_IsFalse_AndNothingIsPublished_WhenNoKeysAreConfigured()
    {
        using var store = StoreWith();

        Assert.False(store.HasSigningKey);
        Assert.Null(store.ActiveKey);
        Assert.Empty(store.PublishedKeys);
        Assert.Null(store.CreateClientAssertion("https://warptalk.test/c.json", "https://auth.test/token", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void MalformedKey_IsSkipped_RatherThanFailingTheWholeStore()
    {
        // Degrading to token_endpoint_auth_method=none keeps every plugin working; throwing here
        // would take down plugins that never needed a key at all.
        var (goodPem, good) = NewKey();
        using var _ = good;

        using var store = StoreWith(("broken", "-----BEGIN PRIVATE KEY-----\nnot-a-key\n-----END PRIVATE KEY-----"), ("good", goodPem));

        Assert.True(store.HasSigningKey);
        Assert.Equal("good", store.ActiveKey!.Kid);
        Assert.Single(store.PublishedKeys);
    }

    [Fact]
    public void PublishedKeys_ExposeOnlyPublicParameters_ActiveKeyFirst()
    {
        var (firstPem, first) = NewKey();
        var (secondPem, second) = NewKey();
        using var _ = first;
        using var __ = second;

        using var store = StoreWith(("active", firstPem), ("retiring", secondPem));

        // Both stay published so a rotation overlaps: a server holding a cached JWKS must still
        // find the retiring kid while in-flight assertions drain.
        Assert.Equal(["active", "retiring"], store.PublishedKeys.Select(k => k.Kid));
        Assert.All(store.PublishedKeys, jwk =>
        {
            Assert.Equal("EC", jwk.Kty);
            Assert.Equal("P-256", jwk.Crv);
            Assert.Equal("ES256", jwk.Alg);
            Assert.Equal("sig", jwk.Use);
            Assert.False(string.IsNullOrWhiteSpace(jwk.X));
            Assert.False(string.IsNullOrWhiteSpace(jwk.Y));
        });
    }

    [Fact]
    public void CreateClientAssertion_ProducesAJwsThePublishedKeyVerifies()
    {
        var (pem, key) = NewKey();
        using var _ = key;
        using var store = StoreWith(("k1", pem));

        var assertion = store.CreateClientAssertion(
            "https://warptalk.test/oauth/client-metadata/v1.json",
            "https://auth.example.test/token",
            TimeSpan.FromMinutes(5))!;

        var parts = assertion.Split('.');
        Assert.Equal(3, parts.Length);

        var header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0])).RootElement;
        Assert.Equal("ES256", header.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.GetProperty("typ").GetString());
        Assert.Equal("k1", header.GetProperty("kid").GetString());

        var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1])).RootElement;
        // RFC 7523: the client is both issuer and subject of its own assertion, and the audience
        // is the endpoint it may be spent at.
        Assert.Equal("https://warptalk.test/oauth/client-metadata/v1.json", payload.GetProperty("iss").GetString());
        Assert.Equal("https://warptalk.test/oauth/client-metadata/v1.json", payload.GetProperty("sub").GetString());
        Assert.Equal("https://auth.example.test/token", payload.GetProperty("aud").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("jti").GetString()));
        Assert.True(payload.GetProperty("exp").GetInt64() > payload.GetProperty("iat").GetInt64());

        // Verify the way a server would: rebuild the public key from the published JWK, then check
        // the signature over the literal signing input.
        var jwk = store.PublishedKeys[0];
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = Base64Url.DecodeFromChars(jwk.X),
                Y = Base64Url.DecodeFromChars(jwk.Y),
            },
        });

        Assert.True(verifier.VerifyData(
            Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"),
            Base64Url.DecodeFromChars(parts[2]),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void CreateClientAssertion_UsesAFreshJtiEachTime()
    {
        var (pem, key) = NewKey();
        using var _ = key;
        using var store = StoreWith(("k1", pem));

        static string Jti(string assertion) =>
            JsonDocument.Parse(Base64Url.DecodeFromChars(assertion.Split('.')[1]))
                .RootElement.GetProperty("jti").GetString()!;

        var first = store.CreateClientAssertion("c", "https://auth.test/token", TimeSpan.FromMinutes(5))!;
        var second = store.CreateClientAssertion("c", "https://auth.test/token", TimeSpan.FromMinutes(5))!;

        Assert.NotEqual(Jti(first), Jti(second));
    }
}

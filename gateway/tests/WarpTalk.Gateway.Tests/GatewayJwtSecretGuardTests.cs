using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc.Testing;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// The gateway read <c>Jwt:Secret</c> itself and rejected only null/empty, so it would boot in
/// Production on the <c>CHANGE_ME</c> placeholder committed to <c>appsettings.json</c> in this
/// public repository. Every other service refused to start in that state through
/// <c>AddWarpTalkJwtAuthentication</c>; the gateway — the only component that validates end-user
/// JWTs on proxied routes — was the one place that did not. A publicly known HMAC key there
/// means anyone can mint a token for any user id and any role and have it believed.
/// </summary>
public sealed class GatewayJwtSecretGuardTests
{
    // Deliberately low-entropy and self-describing. An earlier revision used realistic-looking
    // key strings here and the pipeline's "Secret scan for introduced commits" step rejected the
    // whole PR — correctly, because a scanner cannot tell a convincing fake from the real thing.
    // The guard under test only inspects length and the CHANGE_ME / placeholder markers, so
    // padding with a repeated character satisfies it while staying obviously non-secret.
    private const string LengthPadding = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PlaceholderSecret = "CHANGE_ME-test-fixture-" + LengthPadding;
    private const string RealSecret = "gateway-test-fixture-key-" + LengthPadding;

    [Theory]
    [InlineData(PlaceholderSecret)]
    [InlineData("this-one-says-placeholder-and-is-long-enough-to-pass-length")]
    [InlineData("too-short")]
    [InlineData("")]
    public void Production_refuses_to_start_on_an_unusable_signing_key(string secret)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Authenticate(secret, Environments.Production));

        Assert.Contains("JWT secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_starts_on_a_real_signing_key()
    {
        var options = Authenticate(RealSecret, Environments.Production);

        Assert.True(options.TokenValidationParameters.ValidateIssuerSigningKey);
        Assert.True(options.TokenValidationParameters.ValidateIssuer);
        Assert.True(options.TokenValidationParameters.ValidateAudience);
        Assert.True(options.TokenValidationParameters.ValidateLifetime);
    }

    /// <summary>
    /// Rotation must work at the perimeter too. The gateway's hand-rolled setup used the single
    /// <c>IssuerSigningKey</c> property, so a rotation that the backend services accepted was
    /// rejected at the gateway — tokens signed with the previous key bounced.
    /// </summary>
    [Fact]
    public void Gateway_accepts_previous_signing_keys_during_rotation()
    {
        var options = Authenticate(
            RealSecret,
            Environments.Production,
            previousSecrets: new[]
            {
                "a-previous-gateway-signing-key-of-at-least-32-characters",
                "another-previous-gateway-signing-key-over-32-characters"
            });

        var keys = options.TokenValidationParameters.IssuerSigningKeys?.ToList();

        Assert.NotNull(keys);
        Assert.Equal(3, keys!.Count);
    }

    /// <summary>
    /// The committed default must remain a value the Production guard rejects. If someone ever
    /// puts a plausible-looking secret in appsettings.json, the guard silently stops guarding.
    /// </summary>
    [Fact]
    public void Committed_appsettings_secret_is_one_production_will_refuse()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "gateway/src/WarpTalk.Gateway/appsettings.json")));

        var committed = document.RootElement.GetProperty("Jwt").GetProperty("Secret").GetString();

        Assert.False(string.IsNullOrWhiteSpace(committed));
        Assert.Throws<InvalidOperationException>(
            () => Authenticate(committed!, Environments.Production));
    }


    /// <summary>
    /// The binding test: this boots the gateway's OWN composition root. The tests above prove the
    /// shared helper guards correctly; this one proves the gateway actually goes through it.
    /// Reverting Program.cs to its hand-rolled AddJwtBearer makes this the test that fails.
    /// </summary>
    [Fact]
    public void Gateway_host_refuses_to_build_in_production_with_the_placeholder_secret()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("environment", Environments.Production);
                builder.UseSetting("Jwt:Secret", PlaceholderSecret);
            });

        var exception = Assert.ThrowsAny<Exception>(() => factory.Services.GetService<IConfiguration>());
        Assert.Contains("JWT secret", Flatten(exception), StringComparison.OrdinalIgnoreCase);
    }

    private static string Flatten(Exception exception)
    {
        var text = new System.Text.StringBuilder();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            text.AppendLine(current.Message);
        }

        return text.ToString();
    }

    private static JwtBearerOptions Authenticate(
        string secret,
        string environmentName,
        string[]? previousSecrets = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = secret,
            ["Jwt:Issuer"] = "WarpTalk.AuthService",
            ["Jwt:Audience"] = "WarpTalk"
        };

        for (var i = 0; i < (previousSecrets?.Length ?? 0); i++)
        {
            settings[$"Jwt:PreviousSecrets:{i}"] = previousSecrets![i];
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWarpTalkJwtAuthentication(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            new StubHostEnvironment { EnvironmentName = environmentName });

        return services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "warptalk-backend.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = string.Empty;
        public string ApplicationName { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

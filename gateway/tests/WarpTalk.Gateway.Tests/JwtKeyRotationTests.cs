using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.Gateway.Tests;

public sealed class JwtKeyRotationTests
{
    [Fact]
    public void AuthenticationAcceptsActiveAndPreviousSigningKeys()
    {
        const string active = "active-jwt-signing-key-at-least-32-characters";
        const string previousOne = "previous-jwt-signing-key-one-at-least-32-characters";
        const string previousTwo = "previous-jwt-signing-key-two-at-least-32-characters";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = active,
                ["Jwt:Issuer"] = "WarpTalk.AuthService",
                ["Jwt:Audience"] = "WarpTalk",
                ["Jwt:PreviousSecrets:0"] = previousOne,
                ["Jwt:PreviousSecrets:1"] = previousTwo,
            })
            .Build();
        var services = new ServiceCollection();

        services.AddWarpTalkJwtAuthentication(
            configuration,
            new TestHostEnvironment { EnvironmentName = Environments.Production });

        using var provider = services.BuildServiceProvider();
        var options = provider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        var keys = options.TokenValidationParameters.IssuerSigningKeys?.ToList();

        Assert.NotNull(keys);
        Assert.Equal(3, keys.Count);
        Assert.Null(options.TokenValidationParameters.IssuerSigningKey);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = string.Empty;
        public string ApplicationName { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

using System;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using WarpTalk.Shared.Configuration;

namespace WarpTalk.Shared.Extensions;

public static class JwtAuthenticationExtensions
{
    public static IServiceCollection AddWarpTalkJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null,
        Action<JwtBearerOptions>? configure = null)
    {
        var jwtSection = configuration.GetSection(JwtOptions.SectionName);
        var secretKey = jwtSection["Secret"]
                        ?? jwtSection["SecretKey"]
                        ?? configuration["JwtSettings:SecretKey"]
                        ?? configuration["JWT:SecretKey"]
                        ?? Environment.GetEnvironmentVariable("JWT_SECRET");

        var isDevelopment = environment?.IsDevelopment() == true;
        var isInvalid = string.IsNullOrWhiteSpace(secretKey)
                        || secretKey.Length < 32
                        || secretKey.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
                        || secretKey.Contains("placeholder", StringComparison.OrdinalIgnoreCase);

        if (isInvalid && !isDevelopment)
        {
            throw new InvalidOperationException(
                "CRITICAL SECURITY ERROR: JWT secret must be configured, contain at least 32 characters, and must not be a placeholder.");
        }

        if (string.IsNullOrWhiteSpace(secretKey))
        {
            throw new InvalidOperationException(
                "JWT secret is missing. Development may use an explicit local-only value, but no implicit fallback is provided.");
        }

        var packedPreviousSecrets = jwtSection["PreviousSecrets"]
            ?? Environment.GetEnvironmentVariable("JWT_PREVIOUS_SECRETS");
        var previousSecrets = jwtSection
            .GetSection("PreviousSecrets")
            .GetChildren()
            .Select(child => child.Value)
            .Concat((packedPreviousSecrets ?? string.Empty)
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Where(value => !string.Equals(value, secretKey, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (previousSecrets.Any(IsInvalidSecret))
        {
            throw new InvalidOperationException(
                "JWT previous signing secrets must contain at least 32 characters and must not be placeholders.");
        }

        var issuer = jwtSection["Issuer"]
                     ?? configuration["JwtSettings:Issuer"]
                     ?? Environment.GetEnvironmentVariable("JWT_ISSUER")
                     ?? "WarpTalk.AuthService";

        var audience = jwtSection["Audience"]
                       ?? configuration["JwtSettings:Audience"]
                       ?? Environment.GetEnvironmentVariable("JWT_AUDIENCE")
                       ?? "WarpTalk";
        var signingKeys = new[] { secretKey }
            .Concat(previousSecrets)
            .Select(secret => new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)))
            .ToArray();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = issuer,
                    ValidAudience = audience,
                    IssuerSigningKeys = signingKeys,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
                configure?.Invoke(options);
            });

        return services;
    }

    private static bool IsInvalidSecret(string secret) =>
        secret.Length < 32
        || secret.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
        || secret.Contains("placeholder", StringComparison.OrdinalIgnoreCase);
}

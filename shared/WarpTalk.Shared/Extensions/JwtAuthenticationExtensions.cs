using System;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using WarpTalk.Shared.Configuration;

namespace WarpTalk.Shared.Extensions;

public static class JwtAuthenticationExtensions
{
    public static IServiceCollection AddWarpTalkJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var jwtSection = configuration.GetSection(JwtOptions.SectionName);
        var secretKey = jwtSection["Secret"]
                        ?? configuration["JwtSettings:SecretKey"]
                        ?? Environment.GetEnvironmentVariable("JWT_SECRET");

        if (string.IsNullOrWhiteSpace(secretKey))
        {
            throw new InvalidOperationException(
                "CRITICAL SECURITY ERROR: JWT Secret Key is missing in Configuration (Jwt:Secret / JwtSettings:SecretKey) or Environment Variables (JWT_SECRET)!");
        }

        var issuer = jwtSection["Issuer"] 
                     ?? configuration["JwtSettings:Issuer"] 
                     ?? Environment.GetEnvironmentVariable("JWT_ISSUER") 
                     ?? "WarpTalk.AuthService";

        var audience = jwtSection["Audience"] 
                       ?? configuration["JwtSettings:Audience"] 
                       ?? Environment.GetEnvironmentVariable("JWT_AUDIENCE") 
                       ?? "WarpTalk";

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
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey))
                };
            });

        return services;
    }
}

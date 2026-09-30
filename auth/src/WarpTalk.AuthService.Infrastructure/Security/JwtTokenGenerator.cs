using WarpTalk.Shared.PlatformSettings;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using WarpTalk.AuthService.Application.Interfaces.Security;

namespace WarpTalk.AuthService.Infrastructure.Security;

public class JwtTokenGenerator : IJwtTokenGenerator
{
    private readonly IConfiguration _config;
    private readonly IPlatformSettings? _settings;

    public JwtTokenGenerator(IConfiguration config, IPlatformSettings? settings = null)
    {
        _config = config;
        _settings = settings;
    }

    // Live from /admin/settings (security.session.*), with the Jwt section as the deploy-time
    // fallback. Read on every token issued, so a change applies to the next sign-in or refresh;
    // tokens already issued keep the lifetime they were issued with.
    public int AccessTokenExpiryMinutes => Live(PlatformSettingsCatalog.AccessTokenMinutes, _config.GetValue("Jwt:AccessTokenExpiryMinutes", 30));
    public int RefreshTokenExpiryDays => Live(PlatformSettingsCatalog.RefreshTokenDays, _config.GetValue("Jwt:RefreshTokenExpiryDays", 7));

    private int Live(string key, int configured) => _settings?.GetInt32(key, configured) ?? configured;

    public string GenerateAccessToken(Guid userId, string email, bool emailVerified, IEnumerable<string> roles, string? staffRole = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
            _config["Jwt:Secret"] ?? throw new InvalidOperationException("JWT Secret not configured")));

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("email_verified", emailVerified.ToString().ToLowerInvariant())
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        if (!string.IsNullOrWhiteSpace(staffRole))
        {
            claims.Add(new Claim(WarpTalk.Shared.Authorization.StaffClaims.StaffRole, staffRole));
        }

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(AccessTokenExpiryMinutes),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateRefreshToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
    }
}

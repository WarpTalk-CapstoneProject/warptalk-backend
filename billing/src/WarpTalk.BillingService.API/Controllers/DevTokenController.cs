using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace WarpTalk.BillingService.API.Controllers;

/// <summary>
/// [DEV ONLY] Generate a temporary JWT token for Swagger testing.
/// This controller is disabled in Production environments.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class DevTokenController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _env;

    public DevTokenController(IConfiguration configuration, IWebHostEnvironment env)
    {
        _configuration = configuration;
        _env = env;
    }

    /// <summary>[DEV ONLY] Generate a test JWT token for Swagger authentication</summary>
    /// <remarks>
    /// Generates a short-lived JWT token for local development testing only.
    /// **This endpoint is disabled in Production.**
    ///
    /// Use the returned token in the Swagger Authorize dialog:
    /// Click "Authorize" → Enter: `Bearer {token}`
    /// </remarks>
    [HttpPost("token")]
    [ProducesResponseType(typeof(DevTokenResponse), 200)]
    [ProducesResponseType(403)]
    public IActionResult GenerateToken([FromBody] DevTokenRequest request)
    {
        if (!_env.IsDevelopment())
            return StatusCode(403, new { message = "This endpoint is only available in Development environment." });

        var jwtSettings = _configuration.GetSection("Jwt");
        var secretKey = Environment.GetEnvironmentVariable("JWT__SecretKey") ?? jwtSettings["SecretKey"];
        var issuer = Environment.GetEnvironmentVariable("JWT__Issuer") ?? jwtSettings["Issuer"] ?? "WarpTalk";
        var audience = Environment.GetEnvironmentVariable("JWT__Audience") ?? jwtSettings["Audience"] ?? "WarpTalk.API";

        if (string.IsNullOrWhiteSpace(secretKey))
            return StatusCode(500, new { message = "JWT SecretKey not configured." });

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, request.UserId ?? Guid.NewGuid().ToString()),
            new("email", request.Email ?? "dev@warptalk.local"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        // Add roles
        foreach (var role in request.Roles ?? new[] { "user" })
            claims.Add(new Claim("role", role));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiry = DateTime.UtcNow.AddMinutes(request.ExpirationMinutes > 0 ? request.ExpirationMinutes : 60);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: expiry,
            signingCredentials: creds);

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        return Ok(new DevTokenResponse
        {
            Token = tokenString,
            ExpiresAt = expiry,
            UserId = request.UserId ?? claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value,
            Email = request.Email ?? "dev@warptalk.local",
            Roles = request.Roles ?? new[] { "user" },
            SwaggerValue = $"Bearer {tokenString}"
        });
    }
}

public class DevTokenRequest
{
    /// <summary>User ID to embed in the token (optional, auto-generated if empty)</summary>
    public string? UserId { get; set; }

    /// <summary>Email claim for the token</summary>
    public string? Email { get; set; } = "dev@warptalk.local";

    /// <summary>Roles to assign. Use "billing_admin" to test admin endpoints.</summary>
    public string[]? Roles { get; set; } = new[] { "user" };

    /// <summary>Token expiration in minutes (default: 60)</summary>
    public int ExpirationMinutes { get; set; } = 60;
}

public class DevTokenResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string[] Roles { get; set; } = Array.Empty<string>();

    /// <summary>Paste this value directly into Swagger Authorize dialog</summary>
    public string SwaggerValue { get; set; } = string.Empty;
}

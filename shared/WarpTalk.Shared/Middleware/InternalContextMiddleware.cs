using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using WarpTalk.Shared.Interfaces;

namespace WarpTalk.Shared.Middleware;
//validate sign of X-Internal-Context
public class InternalContextMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _sharedSecret;

    public InternalContextMiddleware(RequestDelegate next, string sharedSecret)
    {
        _next = next;
        _sharedSecret = sharedSecret;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Internal-Context", out var headerValues))
        {
            var token = headerValues.ToString();
            if (!string.IsNullOrWhiteSpace(token))
            {
                try
                {
                    var tokenHandler = new JwtSecurityTokenHandler();
                    var key = Encoding.UTF8.GetBytes(_sharedSecret);
                    
                    var validationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(key),
                        ValidateIssuer = false,
                        ValidateAudience = false,
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.Zero
                    };

                    var principal = tokenHandler.ValidateToken(token, validationParameters, out _);

                    context.User = principal;

                    var subClaim = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    var workspaceIdClaim = principal.FindFirst("workspace_id")?.Value;
                    var roleClaim = principal.FindFirst("role")?.Value;
                    var membershipTypeClaim = principal.FindFirst("membership_type")?.Value;

                    if (Guid.TryParse(subClaim, out var userId) && Guid.TryParse(workspaceIdClaim, out var workspaceId))
                    {
                        // Check if user is blacklisted (revoked/banned)
                        var blacklistService = context.RequestServices.GetService(typeof(ITokenBlacklistService)) as ITokenBlacklistService;
                        if (blacklistService != null && await blacklistService.IsUserBlacklistedAsync(userId))
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return;
                        }

                        var workspaceContext = context.RequestServices.GetService(typeof(IWorkspaceContext)) as IWorkspaceContext;
                        workspaceContext?.SetContext(userId, workspaceId, roleClaim, membershipTypeClaim);
                    }
                }
                catch
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }
        }

        await _next(context);
    }
}

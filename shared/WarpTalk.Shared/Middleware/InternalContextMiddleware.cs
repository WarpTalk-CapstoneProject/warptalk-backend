using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;

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
                        ClockSkew = TimeSpan.Zero
                    };

                    var principal = tokenHandler.ValidateToken(token, validationParameters, out _);

                    var subClaim = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    var workspaceIdClaim = principal.FindFirst("workspace_id")?.Value;

                    if (Guid.TryParse(subClaim, out var userId) && Guid.TryParse(workspaceIdClaim, out var workspaceId))
                    {
                        var workspaceContext = context.RequestServices.GetService(typeof(IWorkspaceContext)) as IWorkspaceContext;
                        workspaceContext?.SetContext(userId, workspaceId);
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

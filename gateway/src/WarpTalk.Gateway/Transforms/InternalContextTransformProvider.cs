using Microsoft.AspNetCore.Authentication;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace WarpTalk.Gateway.Transforms;

public class InternalContextTransformProvider : Yarp.ReverseProxy.Transforms.Builder.ITransformProvider
{
    private readonly byte[] _signingKey;

    public InternalContextTransformProvider(
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var rawSecret = configuration["Jwt:InternalSecret"]
                        ?? configuration["Grpc:InternalSecret"];
        var isInvalid = string.IsNullOrWhiteSpace(rawSecret)
                        || rawSecret.Length < 32
                        || rawSecret.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
                        || rawSecret.Contains("placeholder", StringComparison.OrdinalIgnoreCase);
        if (isInvalid && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "CRITICAL SECURITY ERROR: Jwt:InternalSecret or Grpc:InternalSecret must contain at least 32 characters and must not be a placeholder.");
        }
        if (string.IsNullOrWhiteSpace(rawSecret))
        {
            throw new InvalidOperationException(
                "An explicit internal signing secret is required, including in Development.");
        }

        _signingKey = Encoding.UTF8.GetBytes(rawSecret);
    }

    public void ValidateRoute(TransformRouteValidationContext context)
    {
    }

    public void ValidateCluster(TransformClusterValidationContext context)
    {
    }

    public void Apply(TransformBuilderContext context)
    {
        context.AddRequestTransform(transformContext =>
        {
            var claims = BuildInternalClaims(transformContext.HttpContext);
            if (claims is null)
            {
                return ValueTask.CompletedTask;
            }

            var tokenHandler = new JwtSecurityTokenHandler();
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(_signingKey),
                    SecurityAlgorithms.HmacSha256Signature)
            };

            var jwtString = tokenHandler.WriteToken(tokenHandler.CreateToken(tokenDescriptor));

            transformContext.ProxyRequest.Headers.Remove(InternalContextHeaderName);
            transformContext.ProxyRequest.Headers.Add(InternalContextHeaderName, jwtString);

            return ValueTask.CompletedTask;
        });
    }

    public const string InternalContextHeaderName = "X-Internal-Context";

    /// <summary>
    /// The claims the gateway is willing to vouch for, or <c>null</c> when it should vouch for
    /// nothing. Everything here must come from the validated access token.
    ///
    /// This used to fall back to the <c>active_workspace_id</c> COOKIE when the token carried no
    /// <c>workspace_id</c> claim. The browser sets that cookie, so the caller chose the value —
    /// and the gateway then signed it into <c>X-Internal-Context</c> with the internal secret,
    /// laundering a string the attacker picked into one downstream services are told to trust
    /// precisely because it carries a valid signature. <c>document.cookie =
    /// 'active_workspace_id=&lt;any guid&gt;'</c> was the whole exploit.
    ///
    /// To be precise about what it cost as shipped: <c>JwtTokenGenerator</c> never emits a
    /// <c>workspace_id</c> claim, so the claim branch was always null and the cookie was the only
    /// source; and <c>InternalContextMiddleware</c>, the sole consumer, is registered in no
    /// service. It was armed, not firing. Dropping the fallback means the header is not emitted
    /// until an access token genuinely carries a workspace_id — the honest state, and it stops
    /// whoever eventually wires up that middleware from inheriting cross-tenant access.
    ///
    /// Public so a test can hold this to claims-only without standing up YARP.
    /// </summary>
    public static IReadOnlyList<Claim>? BuildInternalClaims(HttpContext httpContext)
    {
        var user = httpContext.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var sub = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value;
        var workspaceId = user.FindFirst("workspace_id")?.Value;

        if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(workspaceId))
        {
            return null;
        }

        var claims = new List<Claim>
        {
            new("sub", sub),
            new("workspace_id", workspaceId)
        };

        var role = user.FindFirst("role")?.Value ?? user.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.IsNullOrEmpty(role))
        {
            claims.Add(new("role", role));
        }

        var membershipType = user.FindFirst("membership_type")?.Value;
        if (!string.IsNullOrEmpty(membershipType))
        {
            claims.Add(new("membership_type", membershipType));
        }

        return claims;
    }
}

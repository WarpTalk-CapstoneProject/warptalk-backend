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
        context.AddRequestTransform(async transformContext =>
        {
            var httpContext = transformContext.HttpContext;
            var user = httpContext.User;

            if (user?.Identity?.IsAuthenticated == true)
            {
                var sub = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value;
                var workspaceId = user.FindFirst("workspace_id")?.Value
                    ?? httpContext.Request.Cookies["active_workspace_id"];
                var role = user.FindFirst("role")?.Value ?? user.FindFirst(ClaimTypes.Role)?.Value;
                var membershipType = user.FindFirst("membership_type")?.Value;

                if (!string.IsNullOrEmpty(sub) && !string.IsNullOrEmpty(workspaceId))
                {
                    var tokenHandler = new JwtSecurityTokenHandler();
                    var claims = new List<Claim>
                    {
                        new("sub", sub),
                        new("workspace_id", workspaceId)
                    };

                    if (!string.IsNullOrEmpty(role))
                    {
                        claims.Add(new("role", role));
                    }
                    if (!string.IsNullOrEmpty(membershipType))
                    {
                        claims.Add(new("membership_type", membershipType));
                    }

                    var tokenDescriptor = new SecurityTokenDescriptor
                    {
                        Subject = new ClaimsIdentity(claims),
                        Expires = DateTime.UtcNow.AddMinutes(5),
                        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(_signingKey), SecurityAlgorithms.HmacSha256Signature)
                    };

                    var token = tokenHandler.CreateToken(tokenDescriptor);
                    var jwtString = tokenHandler.WriteToken(token);

                    transformContext.ProxyRequest.Headers.Remove("X-Internal-Context");
                    transformContext.ProxyRequest.Headers.Add("X-Internal-Context", jwtString);
                }
            }
        });
    }
}

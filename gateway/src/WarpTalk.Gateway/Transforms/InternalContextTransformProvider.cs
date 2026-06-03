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
    private readonly IConfiguration _configuration;

    public InternalContextTransformProvider(IConfiguration configuration)
    {
        _configuration = configuration;
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
                var workspaceId = user.FindFirst("workspace_id")?.Value;
                var role = user.FindFirst("role")?.Value ?? user.FindFirst(ClaimTypes.Role)?.Value;
                var membershipType = user.FindFirst("membership_type")?.Value;

                if (!string.IsNullOrEmpty(sub) && !string.IsNullOrEmpty(workspaceId))
                {
                    var tokenHandler = new JwtSecurityTokenHandler();
                    var rawSecret = _configuration["Jwt:InternalSecret"] ?? _configuration["Grpc:InternalSecret"] ?? "CHANGE_ME_INTERNAL_SECRET_MIN_32_CHARS_LONG!!";
                    var key = Encoding.UTF8.GetBytes(rawSecret);

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
                        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
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

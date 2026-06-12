using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace WarpTalk.WorkspaceService.Tests;

public static class TokenGeneratorHelper
{
    public static string GenerateInternalSignedToken(Guid userId, Guid workspaceId, string secret, DateTime? expires = null)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(secret);
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim("sub", userId.ToString()),
                new Claim("workspace_id", workspaceId.ToString())
            }),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };

        if (expires.HasValue && expires.Value < DateTime.UtcNow)
        {
            tokenDescriptor.NotBefore = expires.Value.AddMinutes(-5);
            tokenDescriptor.IssuedAt = expires.Value.AddMinutes(-5);
        }

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }
}

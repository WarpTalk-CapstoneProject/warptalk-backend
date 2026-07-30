using System;
using System.Collections.Generic;

namespace WarpTalk.AuthService.Application.Interfaces.Security;

public interface IJwtTokenGenerator
{
    string GenerateAccessToken(Guid userId, string email, bool emailVerified, IEnumerable<string> roles);
    string GenerateRefreshToken();
    int AccessTokenExpiryMinutes { get; }
    int RefreshTokenExpiryDays { get; }
}

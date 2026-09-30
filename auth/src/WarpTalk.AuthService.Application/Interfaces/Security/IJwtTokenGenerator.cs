using System;
using System.Collections.Generic;

namespace WarpTalk.AuthService.Application.Interfaces.Security;

public interface IJwtTokenGenerator
{
    /// <param name="staffRole">G10: the staff role slug, written as the staff_role claim. A UI hint only.</param>
    string GenerateAccessToken(Guid userId, string email, bool emailVerified, IEnumerable<string> roles, string? staffRole = null);
    string GenerateRefreshToken();
    int AccessTokenExpiryMinutes { get; }
    int RefreshTokenExpiryDays { get; }
}

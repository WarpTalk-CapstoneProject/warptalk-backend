namespace WarpTalk.AuthService.Application.Interfaces;

public interface IJwtTokenGenerator
{
    string GenerateAccessToken(Guid userId, string email, IEnumerable<string> roles);
    string GenerateRefreshToken();
    int AccessTokenExpiryMinutes { get; }
    int RefreshTokenExpiryDays { get; }
}

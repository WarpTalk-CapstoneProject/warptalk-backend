using System.Security.Cryptography;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Services;

public class AuthService : IAuthService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _jwtGenerator;
    private readonly IGoogleAuthService _googleAuthService;

    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public AuthService(
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator jwtGenerator,
        IGoogleAuthService googleAuthService)
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _jwtGenerator = jwtGenerator;
        _googleAuthService = googleAuthService;
    }

    public async Task<Result<AuthResponse>> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        if (await _unitOfWork.Users.AnyAsync(u => u.Email == request.Email.ToLowerInvariant().Trim(), ct))
            return Result.Failure<AuthResponse>("Email already registered", ErrorCodes.EmailExists);

        var user = new User
        {
            Email = request.Email.ToLowerInvariant().Trim(),
            PasswordHash = _passwordHasher.Hash(request.Password),
            FullName = request.FullName.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _unitOfWork.Users.AddAsync(user, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        var roles = new List<string> { "member" };
        var (accessToken, refreshToken, expiresAt) = GenerateTokens(user, roles);

        var token = CreateRefreshTokenEntity(user.Id, refreshToken, null, null);
        await _unitOfWork.RefreshTokens.AddAsync(token, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new AuthResponse(accessToken, refreshToken, expiresAt, MapToDto(user, roles)));
    }

    public async Task<Result<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var email = request.Email.ToLowerInvariant().Trim();
        var user = await _unitOfWork.Users.FirstOrDefaultAsync(u => u.Email == email, "UserRoleUsers.Role", ct);
        if (user is null)
            return Result.Failure<AuthResponse>("Invalid email or password", ErrorCodes.InvalidCredentials);

        if (!user.IsActive)
            return Result.Failure<AuthResponse>("Account is deactivated", ErrorCodes.AccountInactive);

        if (user.IsLocked && user.LockedUntil > DateTime.UtcNow)
            return Result.Failure<AuthResponse>($"Account locked until {user.LockedUntil:u}", ErrorCodes.AccountLocked);

        if (!_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= MaxFailedAttempts)
            {
                user.IsLocked = true;
                user.LockedUntil = DateTime.UtcNow.Add(LockoutDuration);
            }
            _unitOfWork.Users.Update(user);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Failure<AuthResponse>("Invalid email or password", ErrorCodes.InvalidCredentials);
        }

        // Reset lockout on successful login
        user.FailedLoginAttempts = 0;
        user.IsLocked = false;
        user.LockedUntil = null;
        user.LastLoginAt = DateTime.UtcNow;
        user.LastLoginIp = request.IpAddress;
        user.UpdatedAt = DateTime.UtcNow;
        _unitOfWork.Users.Update(user);

        var roles = user.UserRoleUsers
            .Select(ur => ur.Role?.Name ?? "member")
            .Distinct()
            .ToList();
        if (roles.Count == 0) roles.Add("member");

        var (accessToken, refreshToken, expiresAt) = GenerateTokens(user, roles);
        var token = CreateRefreshTokenEntity(user.Id, refreshToken, request.IpAddress, request.DeviceInfo);
        await _unitOfWork.RefreshTokens.AddAsync(token, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new AuthResponse(accessToken, refreshToken, expiresAt, MapToDto(user, roles)));
    }

    public async Task<Result<AuthResponse>> RefreshTokenAsync(RefreshTokenRequest request, CancellationToken ct = default)
    {
        var tokenHash = HashToken(request.RefreshToken);
        var storedToken = await _unitOfWork.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, "", ct);

        if (storedToken is null || storedToken.RevokedAt is not null || storedToken.ExpiresAt < DateTime.UtcNow)
            return Result.Failure<AuthResponse>("Invalid or expired refresh token", ErrorCodes.InvalidToken);

        // Revoke old token
        storedToken.RevokedAt = DateTime.UtcNow;
        _unitOfWork.RefreshTokens.Update(storedToken);

        var user = await _unitOfWork.Users.FirstOrDefaultAsync(u => u.Id == storedToken.UserId, "UserRoleUsers.Role", ct);
        if (user is null || !user.IsActive)
            return Result.Failure<AuthResponse>("User not found or inactive", ErrorCodes.UserInactive);

        var roles = user.UserRoleUsers
            .Select(ur => ur.Role?.Name ?? "member")
            .Distinct()
            .ToList();
        if (roles.Count == 0) roles.Add("member");

        var (accessToken, newRefresh, expiresAt) = GenerateTokens(user, roles);
        var newToken = CreateRefreshTokenEntity(user.Id, newRefresh, request.IpAddress, request.DeviceInfo);
        await _unitOfWork.RefreshTokens.AddAsync(newToken, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new AuthResponse(accessToken, newRefresh, expiresAt, MapToDto(user, roles)));
    }

    public async Task<Result> LogoutAsync(Guid userId, string refreshToken, CancellationToken ct = default)
    {
        var tokenHash = HashToken(refreshToken);
        var storedToken = await _unitOfWork.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, "", ct);

        if (storedToken is not null && storedToken.UserId == userId)
        {
            storedToken.RevokedAt = DateTime.UtcNow;
            _unitOfWork.RefreshTokens.Update(storedToken);
            await _unitOfWork.SaveChangesAsync(ct);
        }

        return Result.Success();
    }

    public async Task<Result<UserDto>> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _unitOfWork.Users.FirstOrDefaultAsync(u => u.Id == userId, "UserRoleUsers.Role", ct);
        if (user is null)
            return Result.Failure<UserDto>("User not found", ErrorCodes.UserNotFound);

        var roles = user.UserRoleUsers
            .Select(ur => ur.Role?.Name ?? "member")
            .Distinct()
            .ToList();
        if (roles.Count == 0) roles.Add("member");

        return Result.Success(MapToDto(user, roles));
    }

    public async Task<Result<UserDto>> UpdateProfileAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct = default)
    {
        var user = await _unitOfWork.Users.FirstOrDefaultAsync(u => u.Id == userId, "UserRoleUsers.Role", ct);
        if (user is null)
            return Result.Failure<UserDto>("User not found", ErrorCodes.UserNotFound);

        if (request.FullName is not null) user.FullName = request.FullName.Trim();
        if (request.Phone is not null) user.Phone = request.Phone.Trim();
        if (request.PreferredLanguage is not null) user.PreferredLanguage = request.PreferredLanguage;
        if (request.Timezone is not null) user.Timezone = request.Timezone;
        user.UpdatedAt = DateTime.UtcNow;

        _unitOfWork.Users.Update(user);
        await _unitOfWork.SaveChangesAsync(ct);

        var roles = user.UserRoleUsers
            .Select(ur => ur.Role?.Name ?? "member")
            .Distinct()
            .ToList();
        if (roles.Count == 0) roles.Add("member");

        return Result.Success(MapToDto(user, roles));
    }

    public async Task<Result<AuthResponse>> GoogleLoginAsync(GoogleLoginRequest request, CancellationToken ct = default)
    {
        var payload = await _googleAuthService.VerifyGoogleTokenAsync(request.IdToken, ct);
        if (payload is null)
            return Result.Failure<AuthResponse>("Invalid Google token", ErrorCodes.InvalidToken);

        var email = payload.Email.ToLowerInvariant().Trim();
        var user = await _unitOfWork.Users.FirstOrDefaultAsync(u => u.Email == email, "UserRoleUsers.Role", ct);

        if (user is null)
        {
            user = new User
            {
                Email = email,
                PasswordHash = "", 
                FullName = payload.Name ?? "Google User",
                AvatarUrl = payload.Picture,
                EmailVerified = payload.EmailVerified,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                GoogleId = payload.Subject
            };

            await _unitOfWork.Users.AddAsync(user, ct);
        }
        else
        {
            if (user.GoogleId == null)
            {
                user.GoogleId = payload.Subject;
                if (string.IsNullOrEmpty(user.AvatarUrl)) user.AvatarUrl = payload.Picture;
                if (!user.EmailVerified) user.EmailVerified = payload.EmailVerified;
                _unitOfWork.Users.Update(user);
            }
            if (!user.IsActive)
                return Result.Failure<AuthResponse>("Account is deactivated", ErrorCodes.AccountInactive);
        }

        user.LastLoginAt = DateTime.UtcNow;
        user.LastLoginIp = request.IpAddress;
        
        // We know we must save in case of new user or updated
        if (user.Id != Guid.Empty) _unitOfWork.Users.Update(user);
        await _unitOfWork.SaveChangesAsync(ct);

        var roles = user.UserRoleUsers?
            .Select(ur => ur.Role?.Name ?? "member")
            .Distinct()
            .ToList() ?? new List<string>();
        if (roles.Count == 0) roles.Add("member");

        var (accessToken, refreshToken, expiresAt) = GenerateTokens(user, roles);
        var token = CreateRefreshTokenEntity(user.Id, refreshToken, request.IpAddress, request.DeviceInfo);
        await _unitOfWork.RefreshTokens.AddAsync(token, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new AuthResponse(accessToken, refreshToken, expiresAt, MapToDto(user, roles)));
    }

    public async Task<Result> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default)
    {
        var user = await _unitOfWork.Users.FirstOrDefaultAsync(u => u.Id == userId, "", ct);
        if (user is null)
            return Result.Failure("User not found", ErrorCodes.UserNotFound);

        // allow empty PasswordHash if user was created via Google and has no standard password yet
        if (!string.IsNullOrEmpty(user.PasswordHash) && !_passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
            return Result.Failure("Invalid current password", ErrorCodes.InvalidPassword);

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;

        _unitOfWork.Users.Update(user);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success();
    }

    // --- Private helpers ---

    private (string AccessToken, string RefreshToken, DateTime ExpiresAt) GenerateTokens(User user, List<string> roles)
    {
        var accessToken = _jwtGenerator.GenerateAccessToken(user.Id, user.Email, roles);
        var refreshToken = _jwtGenerator.GenerateRefreshToken();
        var expiresAt = DateTime.UtcNow.AddMinutes(_jwtGenerator.AccessTokenExpiryMinutes);
        return (accessToken, refreshToken, expiresAt);
    }

    private RefreshToken CreateRefreshTokenEntity(Guid userId, string rawToken, string? ip, string? device)
    {
        return new RefreshToken
        {
            UserId = userId,
            TokenHash = HashToken(rawToken),
            ExpiresAt = DateTime.UtcNow.AddDays(_jwtGenerator.RefreshTokenExpiryDays),
            IpAddress = ip,
            DeviceInfo = device,
            CreatedAt = DateTime.UtcNow
        };
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(bytes);
    }

    private static UserDto MapToDto(User user, List<string> roles) => new(
        user.Id,
        user.Email,
        user.FullName,
        user.AvatarUrl,
        user.Phone,
        user.PreferredLanguage,
        user.Timezone,
        user.EmailVerified,
        roles.AsReadOnly()
    );
}

using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Interfaces;

public interface IAuthService
{
    Task<Result<RegisterResponse>> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<Result<AuthResponse>> RegisterInvitedAsync(RegisterInvitedRequest request, CancellationToken ct = default);
    Task<Result<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<Result> ResendVerificationAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// WT-597: a new verification link for an address, with no session behind the request.
    ///
    /// <see cref="ResendVerificationAsync"/> needs a user id, which comes from a token, which a
    /// self-registered account does not get until it is verified (BR-02). So the only resend the
    /// product had was unreachable by exactly the people who needed it: an account whose first
    /// verification mail failed to arrive had no way forward from inside the product at all.
    ///
    /// Always reports success. An unknown, already-verified or disabled address is answered
    /// identically to a real one, so this cannot be used to test whether an address has an
    /// account. Rate limiting still applies underneath — it just is not visible in the answer.
    /// </summary>
    Task<Result> ResendVerificationByEmailAsync(string email, CancellationToken ct = default);
    Task<Result> VerifyEmailAsync(VerifyEmailRequest request, CancellationToken ct = default);
    Task<Result> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct = default);
    Task<Result> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default);
}

using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;
using Xunit;

namespace WarpTalk.AuthService.Tests.Application;

/// <summary>
/// BR-02 — registration must not be a way around the login gate.
///
/// `RegisterAsync` returned a full <see cref="AuthResponse"/> unconditionally and the controller
/// wrote auth cookies from it, so a brand-new account was signed in before its email had been
/// verified. Login already refused an unverified account — `UserStatusHelper` treats it as pending
/// — so the two paths disagreed: the door was locked and the window was open.
///
/// The shape is what these tests pin. `RegisterResponse.Auth` is null exactly when verification is
/// outstanding, so a caller that forgets to check fails loudly at the cookie write rather than
/// installing a session made of empty strings.
/// </summary>
public class RegisterVerificationGateTests
{
    [Fact]
    public void APendingRegistrationCarriesNoSession()
    {
        var response = new RegisterResponse(EmailVerificationRequired: true, Auth: null);

        Assert.Null(response.Auth);
        Assert.True(response.EmailVerificationRequired);
    }

    [Fact]
    public void AVerifiedRegistrationCarriesOne()
    {
        // AutoVerifySelfRegistration on: the address is already proven, so a session is correct
        // and withholding it would lock out every deployment that uses that setting.
        var auth = new AuthResponse("access", "refresh", System.DateTime.UtcNow.AddMinutes(30), null!);

        var response = new RegisterResponse(EmailVerificationRequired: false, Auth: auth);

        Assert.NotNull(response.Auth);
        Assert.False(response.EmailVerificationRequired);
    }

    [Fact]
    public void TheTwoStatesCannotBeConfused()
    {
        // A nullable Auth rather than an AuthResponse full of blanks: the difference between
        // "no session" and "a session whose access token is an empty string" is the difference
        // between a clear failure and a browser holding a credential that can never work.
        var pending = new RegisterResponse(EmailVerificationRequired: true, Auth: null);

        Assert.Throws<System.NullReferenceException>(() => pending.Auth!.AccessToken);
    }
}

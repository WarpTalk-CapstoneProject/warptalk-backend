using System;
using System.Security.Cryptography;
using System.Text;
using WarpTalk.AuthService.Infrastructure.Security;
using Xunit;

namespace WarpTalk.AuthService.Tests;

public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher;

    public PasswordHasherTests()
    {
        _hasher = new PasswordHasher();
    }

    [Fact]
    public void Hash_ShouldReturnFormattedStringWithMetadata()
    {
        // Arrange
        var password = "SuperSecretPassword123!";

        // Act
        var result = _hasher.Hash(password);

        // Assert
        Assert.NotNull(result);
        Assert.StartsWith("v2$", result);

        var parts = result.Split('$');
        Assert.Equal(6, parts.Length);
        Assert.Equal("v2", parts[0]);
        Assert.Equal("SHA512", parts[1]); // Algorithm
        Assert.Equal("100000", parts[2]); // Iterations
        Assert.Equal("16", parts[3]);     // Salt size
    }

    [Fact]
    public void Verify_ShouldSucceed_WithCorrectPassword()
    {
        // Arrange
        var password = "SuperSecretPassword123!";
        var hash = _hasher.Hash(password);

        // Act
        var isValid = _hasher.Verify(password, hash);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void Verify_ShouldFail_WithIncorrectPassword()
    {
        // Arrange
        var password = "SuperSecretPassword123!";
        var hash = _hasher.Hash(password);

        // Act
        var isValid = _hasher.Verify("wrong_password", hash);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void Verify_ShouldBeBackwardCompatible_WithOldDotSeparatedFormat()
    {
        // Arrange: Generate legacy hash manually
        var password = "LegacyPassword123!";
        var salt = RandomNumberGenerator.GetBytes(16);
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA512, 32);
        var legacyHash = $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hashBytes)}";

        // Act
        var isValid = _hasher.Verify(password, legacyHash);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void Verify_ShouldBeBackwardCompatible_With5PartFormat()
    {
        // Arrange: Generate 5-part hash manually: prefix$iterations$saltSize$saltBase64$hashBase64
        var password = "Legacy5PartPassword123!";
        var salt = RandomNumberGenerator.GetBytes(16);
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA512, 32);
        var legacy5PartHash = $"v2$100000$16${Convert.ToBase64String(salt)}${Convert.ToBase64String(hashBytes)}";

        // Act
        var isValid = _hasher.Verify(password, legacy5PartHash);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void Verify_ShouldFail_WhenFormatIsInvalid()
    {
        // Act & Assert
        Assert.False(_hasher.Verify("password", "invalidhashformat"));
        Assert.False(_hasher.Verify("password", "v2$notanumber$16$salt$hash"));
        Assert.False(_hasher.Verify("password", "v2$100000$notanumber$salt$hash"));
        Assert.False(_hasher.Verify("password", "v2$100000$16$notbase64!$notbase64!"));
    }

    [Fact]
    public void Hash_ShouldUseConfiguredSettings_WhenIOptionsInjected()
    {
        // Arrange
        var settings = new WarpTalk.AuthService.Domain.Settings.PasswordHasherSettings
        {
            SaltSize = 24,
            HashSize = 64,
            Iterations = 150_000,
            VersionPrefix = "custom-v3",
            Algorithm = "SHA256"
        };
        var options = Microsoft.Extensions.Options.Options.Create(settings);
        var customHasher = new PasswordHasher(options);

        // Act
        var result = customHasher.Hash("mypassword");

        // Assert
        Assert.StartsWith("custom-v3$", result);
        var parts = result.Split('$');
        Assert.Equal(6, parts.Length);
        Assert.Equal("custom-v3", parts[0]);
        Assert.Equal("SHA256", parts[1]);
        Assert.Equal("150000", parts[2]);
        Assert.Equal("24", parts[3]);

        // Verifying with the custom hasher should succeed
        Assert.True(customHasher.Verify("mypassword", result));
    }
}


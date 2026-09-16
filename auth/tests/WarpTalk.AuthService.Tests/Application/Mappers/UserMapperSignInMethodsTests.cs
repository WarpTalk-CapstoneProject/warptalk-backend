using System;
using System.Collections.Generic;
using System.Text.Json;
using WarpTalk.AuthService.Application.Mappers;
using WarpTalk.AuthService.Domain.Entities;
using Xunit;

namespace WarpTalk.AuthService.Tests.Application.Mappers;

/// <summary>
/// Settings > Connected accounts reads GoogleLinked/HasPassword off GET /auth/me. HasPassword must
/// use the same emptiness rule UnlinkGoogleAsync enforces, or the page would offer an Unlink the
/// server refuses (Google sign-up stores PasswordHash = "", not null).
/// </summary>
public class UserMapperSignInMethodsTests
{
    private static User NewUser(string? passwordHash, string? googleId) => new()
    {
        Id = Guid.NewGuid(),
        Email = "user@example.com",
        FullName = "User",
        PasswordHash = passwordHash,
        GoogleId = googleId,
        IsActive = true,
    };

    [Theory]
    [InlineData("hash", "google-sub", true, true)]
    [InlineData("", "google-sub", true, false)]
    [InlineData(null, "google-sub", true, false)]
    [InlineData("hash", null, false, true)]
    [InlineData("hash", "", false, true)]
    public void Maps_sign_in_methods(string? passwordHash, string? googleId, bool googleLinked, bool hasPassword)
    {
        var dto = UserMapper.ToDto(NewUser(passwordHash, googleId), new List<string> { "user" });

        Assert.Equal(googleLinked, dto.GoogleLinked);
        Assert.Equal(hasPassword, dto.HasPassword);
    }

    [Fact]
    public void Serialized_dto_never_carries_the_google_subject_or_hash()
    {
        var dto = UserMapper.ToDto(NewUser("secret-hash", "google-sub-123"), new List<string> { "user" });
        var json = JsonSerializer.Serialize(dto);

        Assert.DoesNotContain("google-sub-123", json);
        Assert.DoesNotContain("secret-hash", json);
    }
}

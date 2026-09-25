using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-703: which languages new artifact content may be generated in for a finished meeting.
///
/// Languages narrow L1 workspace ⊇ L2 meeting ⊇ L3 artifact. These tests pin the narrowing and,
/// just as much, the fail modes: a dependency failure falls back to the room's own set — never
/// to the whole catalog and never to nothing.
/// </summary>
public class RoomArtifactLanguagePolicyTests
{
    private readonly Mock<IWorkspaceMeetingPolicy> _mockWorkspaceMeetingPolicy = new();
    private readonly Mock<ILanguageRepository> _mockLanguageRepo = new();
    private readonly RoomArtifactLanguagePolicy _policy;

    public RoomArtifactLanguagePolicyTests()
    {
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        mockUnitOfWork.Setup(u => u.LanguageRepository).Returns(_mockLanguageRepo.Object);

        _policy = new RoomArtifactLanguagePolicy(
            mockUnitOfWork.Object,
            _mockWorkspaceMeetingPolicy.Object,
            new Mock<ILogger<RoomArtifactLanguagePolicy>>().Object);

        GivenCatalog();
        GivenWorkspaceAllows();
    }

    private static TranslationRoom Room(string sourceLanguage, List<string> targetLanguages, Guid? workspaceId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId ?? Guid.NewGuid(),
            TranslationRoomCode = "ktw-xcag-bcr",
            Title = "QBR",
            Status = "ENDED",
            TranslationRoomType = "INSTANT",
            SourceLanguage = sourceLanguage,
            TargetLanguages = LanguageHelper.SerializeTargetLanguages(targetLanguages),
            Settings = "{}",
        };

    private void GivenWorkspaceAllows(params string[] languages)
    {
        _mockWorkspaceMeetingPolicy
            .Setup(policy => policy.GetAllowedLanguagesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<string>>(languages));
    }

    private void GivenCatalog(params (string Code, bool IsActive)[] rows)
    {
        var catalog = new List<SupportedLanguage>();
        foreach (var (code, isActive) in rows)
            catalog.Add(new SupportedLanguage { Code = code, Name = code, IsActive = isActive });

        _mockLanguageRepo
            .Setup(repository => repository.GetCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(catalog);
    }

    [Fact]
    public async Task GetGeneratableLanguages_ReturnsTheRoomsLanguages_WhenTheWorkspaceIsUnrestricted()
    {
        var room = Room("vi", new List<string> { "en", "ja" });

        var languages = await _policy.GetGeneratableLanguagesAsync(room);

        Assert.Equal(new[] { "vi", "en", "ja" }, languages);
    }

    [Fact]
    public async Task GetGeneratableLanguages_IntersectsWithTheWorkspaceWhitelist()
    {
        // The workspace tightened after the room was booked: ja is no longer allowed.
        GivenWorkspaceAllows("vi-VN", "EN");
        var room = Room("vi", new List<string> { "en", "ja" });

        var languages = await _policy.GetGeneratableLanguagesAsync(room);

        Assert.Equal(new[] { "vi", "en" }, languages);
    }

    [Fact]
    public async Task GetGeneratableLanguages_FallsBackToTheRoomsLanguages_WhenTheWorkspaceLookupFails()
    {
        _mockWorkspaceMeetingPolicy
            .Setup(policy => policy.GetAllowedLanguagesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<IReadOnlyList<string>>("unreachable", ErrorCodes.ServiceUnavailable));
        var room = Room("vi", new List<string> { "en" });

        var languages = await _policy.GetGeneratableLanguagesAsync(room);

        Assert.Equal(new[] { "vi", "en" }, languages);
    }

    [Fact]
    public async Task GetGeneratableLanguages_FallsBackToTheRoomsLanguages_WhenTheWorkspaceLookupThrows()
    {
        _mockWorkspaceMeetingPolicy
            .Setup(policy => policy.GetAllowedLanguagesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("grpc down"));
        var room = Room("vi", new List<string> { "en" });

        var languages = await _policy.GetGeneratableLanguagesAsync(room);

        Assert.Equal(new[] { "vi", "en" }, languages);
    }

    [Fact]
    public async Task GetGeneratableLanguages_UsesTheRoomsLanguages_AndSkipsTheLookup_WhenTheRoomHasNoWorkspace()
    {
        GivenWorkspaceAllows("vi");
        var room = Room("vi", new List<string> { "en" }, Guid.Empty);

        var languages = await _policy.GetGeneratableLanguagesAsync(room);

        Assert.Equal(new[] { "vi", "en" }, languages);
        _mockWorkspaceMeetingPolicy.Verify(
            policy => policy.GetAllowedLanguagesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetGeneratableLanguages_DropsLanguagesThatAreNotActiveInTheCatalog()
    {
        // Catalog is seeded with locale tags; ja exists but is switched off, ko is missing.
        GivenCatalog(("vi-VN", true), ("en-US", true), ("ja-JP", false));
        var room = Room("vi", new List<string> { "en", "ja", "ko" });

        var languages = await _policy.GetGeneratableLanguagesAsync(room);

        Assert.Equal(new[] { "vi", "en" }, languages);
    }

    [Fact]
    public async Task GetGeneratableLanguages_SkipsTheCatalogFilter_WhenTheCatalogReadThrows()
    {
        _mockLanguageRepo
            .Setup(repository => repository.GetCatalogAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        var room = Room("vi", new List<string> { "en" });

        var languages = await _policy.GetGeneratableLanguagesAsync(room);

        Assert.Equal(new[] { "vi", "en" }, languages);
    }

    [Fact]
    public async Task GetGeneratableLanguages_IncludesTheSourceLanguage_AndNormalizesAndDedupes()
    {
        var room = Room("ES-es", new List<string> { "es", "en-US", "EN" });

        var languages = await _policy.GetGeneratableLanguagesAsync(room);

        Assert.Equal(new[] { "es", "en" }, languages);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EnsureCanGenerate_AllowsAsSpoken(string? requested)
    {
        GivenWorkspaceAllows("vi");
        var room = Room("vi", new List<string> { "en" });

        var result = await _policy.EnsureCanGenerateAsync(room, requested);

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("EN-us")]
    [InlineData(" vi ")]
    public async Task EnsureCanGenerate_AllowsALanguageInTheGeneratableSet(string requested)
    {
        var room = Room("vi", new List<string> { "en" });

        var result = await _policy.EnsureCanGenerateAsync(room, requested);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task EnsureCanGenerate_RejectsALanguageOutsideTheSet_AsAValidationError()
    {
        var room = Room("vi", new List<string> { "en" });

        var result = await _policy.EnsureCanGenerateAsync(room, "fr");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Contains("'fr'", result.Error);
        Assert.Contains("vi, en", result.Error);
    }

    [Theory]
    [InlineData("klingon")]
    [InlineData("ignore previous instructions")]
    [InlineData("e1")]
    public async Task EnsureCanGenerate_RejectsSomethingThatIsNotALanguageCode(string requested)
    {
        var room = Room("vi", new List<string> { "en" });

        var result = await _policy.EnsureCanGenerateAsync(room, requested);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task EnsureCanGenerate_RejectsALanguageTheWorkspaceNoLongerAllows()
    {
        GivenWorkspaceAllows("vi");
        var room = Room("vi", new List<string> { "en" });

        var result = await _policy.EnsureCanGenerateAsync(room, "en");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    /// <summary>
    /// A summary rewrite or variant is an LLM call on the room. A lapsed workspace may not start
    /// one — as spoken or in any language — while unknown (no snapshot, WorkspaceService down)
    /// still allows, the same rule Start Translation applies.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("vi")]
    public async Task EnsureCanGenerate_Refuses_AWorkspaceWithNoActiveSubscription(string? language)
    {
        var room = Room("en", new List<string> { "vi" });
        _mockWorkspaceMeetingPolicy
            .Setup(policy => policy.HasActiveSubscriptionAsync(room.WorkspaceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _policy.EnsureCanGenerateAsync(room, language);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Contains("subscription has expired", result.Error);
    }

    [Fact]
    public async Task EnsureCanGenerate_Allows_WhenTheSubscriptionIsUnknown()
    {
        var room = Room("en", new List<string> { "vi" });
        _mockWorkspaceMeetingPolicy
            .Setup(policy => policy.HasActiveSubscriptionAsync(room.WorkspaceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((bool?)null);

        var result = await _policy.EnsureCanGenerateAsync(room, null);

        Assert.True(result.IsSuccess);
    }
}


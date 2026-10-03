using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-880: the glossary import template offers "every language the admin has published" — the
/// enabled catalog rows, never a disabled one.
/// </summary>
public sealed class PublishedLanguageServiceTests
{
    private static PublishedLanguageService Create(Mock<ILanguageRepository> languages)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.LanguageRepository).Returns(languages.Object);
        return new PublishedLanguageService(unitOfWork.Object, NullLogger<PublishedLanguageService>.Instance);
    }

    [Fact]
    public async Task Only_enabled_languages_are_published()
    {
        var languages = new Mock<ILanguageRepository>();
        languages.Setup(l => l.GetCatalogAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SupportedLanguage>
        {
            new() { Code = "en", Name = "English", IsActive = true },
            new() { Code = "ko", Name = "Korean", IsActive = false },
            new() { Code = "vi", Name = "Vietnamese", NativeName = "Tiếng Việt", IsActive = true },
        });

        var result = await Create(languages).GetPublishedAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value!.Select(l => l.Code).Should().Equal("en", "vi");
        result.Value!.Should().OnlyContain(l => l.IsActive);
    }

    [Fact]
    public async Task A_failed_read_is_a_failure_not_an_empty_list()
    {
        var languages = new Mock<ILanguageRepository>();
        languages.Setup(l => l.GetCatalogAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));

        var result = await Create(languages).GetPublishedAsync();

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.InternalServerError);
    }
}

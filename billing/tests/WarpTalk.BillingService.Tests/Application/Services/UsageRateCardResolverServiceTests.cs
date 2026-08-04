using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

public class UsageRateCardResolverServiceTests
{
    private readonly Mock<IUsageRateCardRepository> _mockRepo;
    private readonly IMemoryCache _cache;
    private readonly UsageRateCardResolverService _resolver;

    public UsageRateCardResolverServiceTests()
    {
        _mockRepo = new Mock<IUsageRateCardRepository>();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _resolver = new UsageRateCardResolverService(_mockRepo.Object, _cache);
    }

    [Fact]
    public async Task ResolveRateCardAsync_Level1_ExactMatch()
    {
        var id = Guid.NewGuid();
        var cards = new List<UsageRateCardDto>
        {
            new(Guid.NewGuid(), "transcribe", "minutes", "provider", "model", null, null, 10, "VND", null, null, DateTime.UtcNow, null, true), // Level 4
            new(Guid.NewGuid(), "transcribe", "minutes", "provider", "model", null, "en", 15, "VND", null, null, DateTime.UtcNow, null, true), // Level 2
            new(id, "transcribe", "minutes", "provider", "model", "vi", "en", 20, "VND", null, null, DateTime.UtcNow, null, true) // Level 1
        };
        _mockRepo.Setup(r => r.GetActiveRateCardsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(cards);

        var result = await _resolver.ResolveRateCardAsync("transcribe", "minutes", "VND", "vi", "en");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(id);
    }

    [Fact]
    public async Task ResolveRateCardAsync_Level2_TargetMatch()
    {
        var id = Guid.NewGuid();
        var cards = new List<UsageRateCardDto>
        {
            new(Guid.NewGuid(), "transcribe", "minutes", "provider", "model", null, null, 10, "VND", null, null, DateTime.UtcNow, null, true), // Level 4
            new(id, "transcribe", "minutes", "provider", "model", null, "en", 15, "VND", null, null, DateTime.UtcNow, null, true), // Level 2
            new(Guid.NewGuid(), "transcribe", "minutes", "provider", "model", "vi", "fr", 20, "VND", null, null, DateTime.UtcNow, null, true) // Not match
        };
        _mockRepo.Setup(r => r.GetActiveRateCardsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(cards);

        var result = await _resolver.ResolveRateCardAsync("transcribe", "minutes", "VND", "vi", "en");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(id);
    }

    [Fact]
    public async Task ResolveRateCardAsync_Level3_SourceMatch()
    {
        var id = Guid.NewGuid();
        var cards = new List<UsageRateCardDto>
        {
            new(Guid.NewGuid(), "transcribe", "minutes", "provider", "model", null, null, 10, "VND", null, null, DateTime.UtcNow, null, true), // Level 4
            new(id, "transcribe", "minutes", "provider", "model", "vi", null, 15, "VND", null, null, DateTime.UtcNow, null, true), // Level 3
            new(Guid.NewGuid(), "transcribe", "minutes", "provider", "model", "en", "fr", 20, "VND", null, null, DateTime.UtcNow, null, true) // Not match
        };
        _mockRepo.Setup(r => r.GetActiveRateCardsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(cards);

        var result = await _resolver.ResolveRateCardAsync("transcribe", "minutes", "VND", "vi", "en");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(id);
    }

    [Fact]
    public async Task ResolveRateCardAsync_Level4_BaseMatch()
    {
        var id = Guid.NewGuid();
        var cards = new List<UsageRateCardDto>
        {
            new(id, "transcribe", "minutes", "provider", "model", null, null, 10, "VND", null, null, DateTime.UtcNow, null, true), // Level 4
            new(Guid.NewGuid(), "transcribe", "minutes", "provider", "model", "en", null, 15, "VND", null, null, DateTime.UtcNow, null, true), // Not match
            new(Guid.NewGuid(), "transcribe", "minutes", "provider", "model", "en", "fr", 20, "VND", null, null, DateTime.UtcNow, null, true) // Not match
        };
        _mockRepo.Setup(r => r.GetActiveRateCardsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(cards);

        var result = await _resolver.ResolveRateCardAsync("transcribe", "minutes", "VND", "vi", "en");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(id);
    }

    [Fact]
    public async Task ResolveRateCardAsync_NoMatch_ShouldReturnError()
    {
        var cards = new List<UsageRateCardDto>
        {
            new(Guid.NewGuid(), "transcribe", "minutes", "provider", "model", null, null, 10, "USD", null, null, DateTime.UtcNow, null, true), // Wrong currency
            new(Guid.NewGuid(), "other_type", "minutes", "provider", "model", null, null, 15, "VND", null, null, DateTime.UtcNow, null, true), // Wrong type
        };
        _mockRepo.Setup(r => r.GetActiveRateCardsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(cards);

        var result = await _resolver.ResolveRateCardAsync("transcribe", "minutes", "VND", "vi", "en");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("RATE_CARD_NOT_FOUND");
    }
}


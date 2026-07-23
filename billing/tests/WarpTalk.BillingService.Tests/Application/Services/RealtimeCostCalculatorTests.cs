using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

public class RealtimeCostCalculatorTests
{
    private readonly Mock<IConfiguration> _mockConfig;
    private readonly UsageService _calculator;

    public RealtimeCostCalculatorTests()
    {
        _mockConfig = new Mock<IConfiguration>();

        _mockConfig.Setup(c => c["BillingRates:SttPerSecond"]).Returns("1.0");
        _mockConfig.Setup(c => c["BillingRates:TranslationPer100Chars"]).Returns("1.0");
        _mockConfig.Setup(c => c["BillingRates:StandardTtsPerSecond"]).Returns("1.0");
        _mockConfig.Setup(c => c["BillingRates:VoiceClonePerSecond"]).Returns("1.5");
        _mockConfig.Setup(c => c["BillingRates:AiAssistantInputPer1000Tokens"]).Returns("0.5");
        _mockConfig.Setup(c => c["BillingRates:AiAssistantOutputPer1000Tokens"]).Returns("2.0");

        _calculator = new UsageService(
            null!, 
            null!);
    }

    [Fact]
    public void CalculateCreditCost_WithoutVoiceClone_ShouldUseBaseRates()
    {
        // Arrange
        var plan = new Plan { Tier = "Startup" };

        // Act: 10s STT (10) + 2000 chars translation (20) + 100ms standard TTS (0.1) = 30.1 -> Ceiling = 31
        var cost = _calculator.CalculateCreditCost(audioSeconds: 10, tokenCount: 2000, gpuInferenceMs: 100, isVoiceClone: false, plan: plan);

        // Assert
        cost.Should().Be(31);
    }

    [Fact]
    public void CalculateCreditCost_WithVoiceClone_StartupPlan_ShouldUseVoiceCloneRate()
    {
        // Arrange
        var plan = new Plan { Tier = "Startup" };

        // Act: 10s STT (10) + 2000 chars translation (20) + 100ms cloned TTS (100/1000 * 1.5 = 0.15) = 30.15 -> Ceiling = 31
        var cost = _calculator.CalculateCreditCost(audioSeconds: 10, tokenCount: 2000, gpuInferenceMs: 100, isVoiceClone: true, plan: plan);

        // Assert
        cost.Should().Be(31);
    }

    [Fact]
    public void CalculateCreditCost_WithVoiceClone_EnterprisePlan_ShouldUseVoiceCloneRate()
    {
        // Arrange
        var plan = new Plan { Tier = "Enterprise" };

        // Act: 10s STT (10) + 2000 chars translation (20) + 100ms cloned TTS (100/1000 * 1.5 = 0.15) = 30.15 -> Ceiling = 31
        var cost = _calculator.CalculateCreditCost(audioSeconds: 10, tokenCount: 2000, gpuInferenceMs: 100, isVoiceClone: true, plan: plan);

        // Assert
        cost.Should().Be(31);
    }

    [Fact]
    public void CalculateCreditCost_AllZeroInputs_ShouldReturnMinimumCost()
    {
        // Edge case: zero audio/token/gpu — cost should be at minimum 1 (not 0)
        var plan = new Plan { Tier = "Startup" };

        var cost = _calculator.CalculateCreditCost(audioSeconds: 0, tokenCount: 0, gpuInferenceMs: 0, isVoiceClone: false, plan: plan);

        cost.Should().BeGreaterThanOrEqualTo(1);
    }
}


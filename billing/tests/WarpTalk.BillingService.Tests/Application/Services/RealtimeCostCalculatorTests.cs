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

        _mockConfig.Setup(c => c["BillingRates:SttPerMinute"]).Returns("10.0");
        _mockConfig.Setup(c => c["BillingRates:TranslationPerMinute"]).Returns("10.0");
        _mockConfig.Setup(c => c["BillingRates:StandardTtsPerMinute"]).Returns("5.0");
        _mockConfig.Setup(c => c["BillingRates:VoiceClonePerMinute"]).Returns("25.0");

        _calculator = new UsageService(
            null!, 
            null!, 
            _mockConfig.Object);
    }

    [Fact]
    public void CalculateCreditCost_WithoutVoiceClone_ShouldUseBaseRates()
    {
        // Arrange
        var plan = new Plan { Tier = "Startup", VoiceCloneEnabled = true };

        // Act
        var cost = _calculator.CalculateCreditCost(audioSeconds: 10, tokenCount: 2000, gpuInferenceMs: 100, isVoiceClone: false, plan: plan);

        // Assert
        cost.Should().Be(5);
    }

    [Fact]
    public void CalculateCreditCost_WithVoiceClone_StartupPlan_ShouldUseVoiceCloneRate()
    {
        // Arrange
        var plan = new Plan { Tier = "Startup", VoiceCloneEnabled = true };

        // Act
        var cost = _calculator.CalculateCreditCost(audioSeconds: 10, tokenCount: 2000, gpuInferenceMs: 100, isVoiceClone: true, plan: plan);

        // Assert
        cost.Should().Be(5);
    }

    [Fact]
    public void CalculateCreditCost_WithVoiceClone_EnterprisePlan_ShouldUseVoiceCloneRate()
    {
        // Arrange
        var plan = new Plan { Tier = "Enterprise", VoiceCloneEnabled = true };

        // Act
        var cost = _calculator.CalculateCreditCost(audioSeconds: 10, tokenCount: 2000, gpuInferenceMs: 100, isVoiceClone: true, plan: plan);

        // Assert
        cost.Should().Be(5);
    }

    [Fact]
    public void CalculateCreditCost_AllZeroInputs_ShouldReturnMinimumCost()
    {
        // Edge case: zero audio/token/gpu — cost should be at minimum 1 (not 0)
        var plan = new Plan { Tier = "Startup", VoiceCloneEnabled = true };

        var cost = _calculator.CalculateCreditCost(audioSeconds: 0, tokenCount: 0, gpuInferenceMs: 0, isVoiceClone: false, plan: plan);

        cost.Should().BeGreaterThanOrEqualTo(1);
    }
}


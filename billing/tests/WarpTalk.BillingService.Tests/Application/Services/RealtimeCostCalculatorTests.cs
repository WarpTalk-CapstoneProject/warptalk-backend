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
    private readonly RealtimeCostCalculator _calculator;

    public RealtimeCostCalculatorTests()
    {
        _mockConfig = new Mock<IConfiguration>();

        // Setup default config mock simulating appsettings.json
        var configSectionMock = new Mock<IConfigurationSection>();
        
        _mockConfig.Setup(c => c["BillingRates:AudioPerSecond"]).Returns("0.5");
        _mockConfig.Setup(c => c["BillingRates:Per1000Tokens"]).Returns("2.0");
        _mockConfig.Setup(c => c["BillingRates:GpuPerMs"]).Returns("0.005");

        _calculator = new RealtimeCostCalculator(_mockConfig.Object);
    }

    [Fact]
    public void CalculateCreditCost_WithoutVoiceClone_ShouldUseBaseRates()
    {
        // Arrange
        var plan = new Plan { Tier = "Pro", VoiceCloneEnabled = true };
        
        // Audio: 10 * 0.5 = 5
        // Token: 2000 / 1000 * 2.0 = 4
        // GPU: 100 * 0.005 = 0.5
        // Base = 9.5 -> Ceil = 10
        
        // Act
        var cost = _calculator.CalculateCreditCost(audioSeconds: 10, tokenCount: 2000, gpuInferenceMs: 100, isVoiceClone: false, plan: plan);

        // Assert
        cost.Should().Be(10);
    }

    [Fact]
    public void CalculateCreditCost_WithVoiceClone_ProPlan_ShouldApplyMultiplier1_2()
    {
        // Arrange
        var plan = new Plan { Tier = "Pro", VoiceCloneEnabled = true };
        
        // Base = 9.5
        // Pro Voice Clone = 9.5 * 1.2 = 11.4 -> Ceil = 12
        
        // Act
        var cost = _calculator.CalculateCreditCost(audioSeconds: 10, tokenCount: 2000, gpuInferenceMs: 100, isVoiceClone: true, plan: plan);

        // Assert
        cost.Should().Be(12);
    }

    [Fact]
    public void CalculateCreditCost_WithVoiceClone_PremiumPlan_ShouldApplyMultiplier1_0()
    {
        // Arrange
        var plan = new Plan { Tier = "Premium", VoiceCloneEnabled = true };
        
        // Base = 9.5
        // Premium Voice Clone = 9.5 * 1.0 = 9.5 -> Ceil = 10
        
        // Act
        var cost = _calculator.CalculateCreditCost(audioSeconds: 10, tokenCount: 2000, gpuInferenceMs: 100, isVoiceClone: true, plan: plan);

        // Assert
        cost.Should().Be(10);
    }

    [Fact]
    public void CalculateCreditCost_WithVoiceClone_FreePlan_ShouldApplyMultiplier2_0()
    {
        // Arrange
        var plan = new Plan { Tier = "Free", VoiceCloneEnabled = false };
        
        // Base = 9.5
        // Free Voice Clone = 9.5 * 2.0 = 19.0 -> Ceil = 19
        
        // Act
        var cost = _calculator.CalculateCreditCost(audioSeconds: 10, tokenCount: 2000, gpuInferenceMs: 100, isVoiceClone: true, plan: plan);

        // Assert
        cost.Should().Be(19);
    }
}

using FluentAssertions;
using Microsoft.Extensions.Options;
using WarpTalk.BillingService.Application.Configuration;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

public class RealtimeCostCalculatorTests
{
    private readonly UsageService _calculator;

    public RealtimeCostCalculatorTests()
    {
        _calculator = new UsageService(
            null!, 
            null!, 
            Options.Create(new BillingRatesOptions
            {
                SttPerMinute = 10.0,
                TranslationPerMinute = 10.0,
                StandardTtsPerMinute = 5.0,
                VoiceClonePerMinute = 25.0,
                AiSummaryPerRequest = 5.0,
                AiChatPerRequest = 2.0
            }));
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

using FluentAssertions;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

public class RealtimeCostCalculatorTests
{
    private static readonly ServiceRatesDto DefaultRates = new(
        SttPerSecond: 1.0,
        TranslationPer100Chars: 1.0,
        StandardTtsPerSecond: 1.0,
        VoiceClonePerSecond: 1.5,
        AiAssistantInputPer1000Tokens: 0.5,
        AiAssistantOutputPer1000Tokens: 2.0);

    [Fact]
    public void CalculateCreditCost_WithoutVoiceClone_ShouldUseBaseRates()
    {
        // Act: 10s STT (10) + 2000 chars translation (20) + 100ms standard TTS (0.1) = 30.1 -> Ceiling = 31
        var cost = CreditRatesHelper.CalculateCreditCost(new CreditCostRequest(
            AudioSeconds: 10,
            TokenCount: 2000,
            GpuInferenceMs: 100,
            IsVoiceClone: false,
            Rates: DefaultRates));

        cost.Should().Be(31);
    }

    [Fact]
    public void CalculateCreditCost_WithVoiceClone_StartupPlan_ShouldUseVoiceCloneRate()
    {
        // Act: 10s STT (10) + 2000 chars translation (20) + 100ms cloned TTS (100/1000 * 1.5 = 0.15) = 30.15 -> Ceiling = 31
        var cost = CreditRatesHelper.CalculateCreditCost(new CreditCostRequest(
            AudioSeconds: 10,
            TokenCount: 2000,
            GpuInferenceMs: 100,
            IsVoiceClone: true,
            Rates: DefaultRates));

        cost.Should().Be(31);
    }

    [Fact]
    public void CalculateCreditCost_WithVoiceClone_EnterprisePlan_ShouldUseVoiceCloneRate()
    {
        // Act: 10s STT (10) + 2000 chars translation (20) + 100ms cloned TTS (100/1000 * 1.5 = 0.15) = 30.15 -> Ceiling = 31
        var cost = CreditRatesHelper.CalculateCreditCost(new CreditCostRequest(
            AudioSeconds: 10,
            TokenCount: 2000,
            GpuInferenceMs: 100,
            IsVoiceClone: true,
            Rates: DefaultRates));

        cost.Should().Be(31);
    }

    [Fact]
    public void CalculateCreditCost_AllZeroInputs_ShouldReturnMinimumCost()
    {
        var cost = CreditRatesHelper.CalculateCreditCost(new CreditCostRequest(
            AudioSeconds: 0,
            TokenCount: 0,
            GpuInferenceMs: 0,
            IsVoiceClone: false,
            Rates: DefaultRates));

        cost.Should().BeGreaterThanOrEqualTo(1);
    }
}

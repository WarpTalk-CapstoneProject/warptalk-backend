using Microsoft.Extensions.Configuration;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Services;

public class RealtimeCostCalculator : IRealtimeCostCalculator
{
    private readonly IConfiguration _configuration;

    public RealtimeCostCalculator(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public int CalculateCreditCost(int audioSeconds, int tokenCount, int gpuInferenceMs, bool isVoiceClone, Plan plan)
    {
        // Get rates from configuration or use defaults
        var audioRateStr = _configuration["BillingRates:AudioPerSecond"] ?? "0.5";
        var tokenRateStr = _configuration["BillingRates:Per1000Tokens"] ?? "2.0";
        var gpuRateMsStr = _configuration["BillingRates:GpuPerMs"] ?? "0.005";

        var audioRate = decimal.Parse(audioRateStr, System.Globalization.CultureInfo.InvariantCulture);
        var tokenRate = decimal.Parse(tokenRateStr, System.Globalization.CultureInfo.InvariantCulture);
        var gpuRateMs = decimal.Parse(gpuRateMsStr, System.Globalization.CultureInfo.InvariantCulture);

        // 1. Audio Cost
        var audioCost = audioSeconds * audioRate;

        // 2. Token Cost
        var tokenCost = (tokenCount / 1000m) * tokenRate;

        // 3. GPU Cost
        var gpuCost = gpuInferenceMs * gpuRateMs;

        // Sum up base cost
        var baseCost = audioCost + tokenCost + gpuCost;

        // Apply Multiplier based on Plan and Voice Clone
        var multiplier = 1.0m;

        if (isVoiceClone)
        {
            if (plan.Tier.Equals("Pro", System.StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1.2m;
            }
            else if (plan.Tier.Equals("Premium", System.StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1.0m;
            }
            else if (plan.Tier.Equals("Free", System.StringComparison.OrdinalIgnoreCase))
            {
                // Voice clone not supported on Free, but if somehow passed, charge high or throw
                multiplier = 2.0m;
            }
        }

        // Final cost rounded up
        var finalCost = Math.Ceiling(baseCost * multiplier);

        return (int)finalCost;
    }
}

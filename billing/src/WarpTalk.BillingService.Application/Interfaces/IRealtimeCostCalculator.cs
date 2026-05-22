using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IRealtimeCostCalculator
{
    int CalculateCreditCost(int audioSeconds, int tokenCount, int gpuInferenceMs, bool isVoiceClone, Plan plan);
}

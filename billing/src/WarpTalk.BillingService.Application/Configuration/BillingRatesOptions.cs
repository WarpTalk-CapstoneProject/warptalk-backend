using System.ComponentModel.DataAnnotations;

namespace WarpTalk.BillingService.Application.Configuration;

public sealed class BillingRatesOptions
{
    public const string SectionName = "BillingRates";

    [Range(double.Epsilon, double.MaxValue)]
    public double SttPerMinute { get; init; }

    [Range(double.Epsilon, double.MaxValue)]
    public double TranslationPerMinute { get; init; }

    [Range(double.Epsilon, double.MaxValue)]
    public double StandardTtsPerMinute { get; init; }

    [Range(double.Epsilon, double.MaxValue)]
    public double VoiceClonePerMinute { get; init; }

    [Range(double.Epsilon, double.MaxValue)]
    public double AiSummaryPerRequest { get; init; }

    [Range(double.Epsilon, double.MaxValue)]
    public double AiChatPerRequest { get; init; }
}

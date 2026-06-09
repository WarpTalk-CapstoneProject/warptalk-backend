using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Domain.Settings;

public record AiUsagePolicyConfiguration(
    bool? AllowExternalLlm,
    PiiRedactionConfiguration? RedactPii,
    DlpConfiguration? Dlp,
    TranslationProfileConfiguration? TranslationProfile
);

public record PiiRedactionConfiguration(
    bool Enabled
);

public record DlpConfiguration(
    bool Enabled,
    List<string>? KeywordsBlacklist
);

public record TranslationProfileConfiguration(
    string? TranslationTone,
    LanguageSpecificRules? LanguageSpecificRules
);

public record LanguageSpecificRules(
    string? VietnameseHonorificStyle,
    string? JapaneseHonorificStyle
);

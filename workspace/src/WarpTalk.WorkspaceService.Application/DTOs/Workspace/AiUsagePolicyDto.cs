using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Application.DTOs.Workspace;

public record AiUsagePolicyDto(
    bool? AllowExternalLlm,
    PiiRedactionDto? RedactPii,
    DlpDto? Dlp,
    TranslationProfileDto? TranslationProfile,
    bool? UseGlobalGlossary = null
);

public record PiiRedactionDto(
    bool Enabled
);

public record DlpDto(
    bool Enabled,
    List<string>? KeywordsBlacklist
);

public record TranslationProfileDto(
    string? TranslationTone,
    LanguageSpecificRulesDto? LanguageSpecificRules
);

public record LanguageSpecificRulesDto(
    string? VietnameseHonorificStyle,
    string? JapaneseHonorificStyle
);

using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Domain.Settings;

public record AiUsagePolicyConfiguration(
    bool? AllowExternalLlm,
    PiiRedactionConfiguration? RedactPii,
    DlpConfiguration? Dlp,
    TranslationProfileConfiguration? TranslationProfile,
    // Opt-out semantics (matches AllowExternalLlm): unset ⇒ true. The system-managed global
    // glossary (transcript.global_glossary_terms) is merged into every workspace's STT/MT
    // prompts by default — a workspace sets this false to exclude it. See
    // docs/global-glossary-plan.md §2.4.
    bool? UseGlobalGlossary = null
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

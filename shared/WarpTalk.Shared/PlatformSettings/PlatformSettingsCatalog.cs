using System.Text.Json;

namespace WarpTalk.Shared.PlatformSettings;

/// <summary>
/// Every runtime setting the platform settings console can change, and nothing else.
///
/// THE RULE: a key is added here in the same change that makes its owning service read it through
/// <see cref="IPlatformSettings"/>, with a test that proves the live value is used. A key nobody
/// reads is a switch wired to nothing — the console would show it, an operator would change it, and
/// nothing would happen. The registry test (<c>PlatformSettingsCatalogTests</c>) holds each key to a
/// reader; <see cref="SettingDefinition.OwningService"/> names where to look.
///
/// Secrets and endpoints are never settings: they stay in deploy configuration, and the console only
/// shows whether they are configured. Keys are a contract with stored rows and with the Python
/// workers (warptalk-ai <c>shared/platform_settings.py</c>): add, never rename.
/// </summary>
public static class PlatformSettingsCatalog
{
    private const string EmailPattern = @"^[^@\s<>""]+@[^@\s<>""]+\.[^@\s<>""]+$";
    private const string LanguagePattern = "^[a-z]{2,3}(-[A-Z]{2})?$";
    private const string TimezonePattern = "^(UTC|[A-Za-z]+(/[A-Za-z0-9_+-]+){1,2})$";

    // ── General ─────────────────────────────────────────────────────────────────────────────
    public const string MaintenanceEnabled = "general.maintenance.enabled";
    public const string MaintenanceMessage = "general.maintenance.message";
    public const string MaintenanceAllowlist = "general.maintenance.allowlist_emails";
    public const string SupportEmail = "general.support_email";
    public const string WorkspaceDefaultLanguage = "general.workspace.default_language";
    public const string WorkspaceDefaultTimezone = "general.workspace.default_timezone";

    // ── Security & auth ─────────────────────────────────────────────────────────────────────
    public const string AccessTokenMinutes = "security.session.access_token_minutes";
    public const string RefreshTokenDays = "security.session.refresh_token_days";
    public const string PasswordMinLength = "security.password.min_length";
    public const string LockoutMaxFailedAttempts = "security.lockout.max_failed_attempts";
    public const string LockoutDurationMinutes = "security.lockout.duration_minutes";
    public const string GoogleSignInEnabled = "security.oauth.google_enabled";
    public const string LoginRateLimit = "security.rate_limit.login_per_window";

    // ── Meetings & AI pipeline ──────────────────────────────────────────────────────────────
    public const string FlashModeDefault = "meetings.flash_mode_default";
    public const string ChunkDurationMs = "meetings.chunk_duration_ms";
    public const string SuggestMinWords = "meetings.ai_suggest.min_words";
    public const string SuggestMinConfidence = "meetings.ai_suggest.min_confidence";
    public const string SuggestCooldownSeconds = "meetings.ai_suggest.cooldown_seconds";
    public const string SuggestMaxPerMeeting = "meetings.ai_suggest.max_per_meeting";
    public const string SuggestMinSttConfidence = "meetings.ai_suggest.min_stt_confidence";
    public const string VoiceCloneMinSeconds = "meetings.voice_clone.min_sample_seconds";
    public const string VoiceCloneUpgradeMargin = "meetings.voice_clone.upgrade_margin";
    public const string RecordingEnabled = "meetings.recording.enabled";

    // ── Billing ─────────────────────────────────────────────────────────────────────────────
    public const string TrialDays = "billing.trial.days";
    public const string TrialCredits = "billing.trial.credits";
    public const string FrozenCreditGraceDays = "billing.frozen_credits.grace_days";

    // ── Notifications & email ───────────────────────────────────────────────────────────────
    public const string EmailFromName = "notifications.email.from_name";
    public const string EmailFromAddress = "notifications.email.from_address";
    public const string EmailReplyTo = "notifications.email.reply_to";

    // ── Limits & quotas ─────────────────────────────────────────────────────────────────────
    public const string DocumentUploadMb = "limits.document_upload_mb";
    public const string MeetingCreatePerMinute = "limits.meeting_create_per_minute";
    public const string UserRateLimit = "limits.rate_limit.user_per_window";
    public const string IpRateLimit = "limits.rate_limit.ip_per_window";

    // ── Feature flags ───────────────────────────────────────────────────────────────────────
    public const string FlagAiSuggest = "flags.ai_suggest";
    public const string FlagGlobalGlossary = "flags.global_glossary";
    public const string FlagWarpBotWebSearch = "flags.warpbot_web_search";
    public const string FlagVoiceClone = "flags.voice_clone";

    public static readonly IReadOnlyList<SettingDefinition> All =
    [
        // General
        new()
        {
            Key = MaintenanceEnabled, Category = SettingCategories.General, Type = SettingValueType.Boolean,
            Default = Json(false), OwningService = SettingOwners.Gateway, Risky = true,
            Label = "Maintenance mode",
            Description = "Every API call from anyone not on the maintenance allowlist is answered 503 with the maintenance message, and the web shows the banner. Sign-in, health checks and the admin API stay open so staff can turn it off again.",
        },
        new()
        {
            Key = MaintenanceMessage, Category = SettingCategories.General, Type = SettingValueType.String,
            Default = Json("WarpTalk is undergoing scheduled maintenance. Please try again shortly."), OwningService = SettingOwners.Gateway,
            MaxLength = 500,
            Label = "Maintenance message",
            Description = "Shown in the web banner and returned with every 503 while maintenance mode is on.",
        },
        new()
        {
            Key = MaintenanceAllowlist, Category = SettingCategories.General, Type = SettingValueType.StringList,
            Default = Json(Array.Empty<string>()), OwningService = SettingOwners.Gateway, Sensitive = true,
            MaxLength = 50, Pattern = EmailPattern,
            Label = "Maintenance allowlist",
            Description = "E-mail addresses that keep full access during maintenance, e.g. the staff verifying the release.",
        },
        new()
        {
            Key = SupportEmail, Category = SettingCategories.General, Type = SettingValueType.String,
            Default = Json("support@warptalk.vn"), OwningService = SettingOwners.Web,
            MaxLength = 254, Pattern = EmailPattern,
            Label = "Support e-mail",
            Description = "The contact address the web shows in the maintenance banner and on error pages.",
        },
        new()
        {
            Key = WorkspaceDefaultLanguage, Category = SettingCategories.General, Type = SettingValueType.String,
            Default = Json("en"), OwningService = SettingOwners.Workspace,
            MaxLength = 10, Pattern = LanguagePattern,
            Label = "New workspace language",
            Description = "The default language a new workspace starts with (its invitation e-mails use it). Existing workspaces keep theirs.",
        },
        new()
        {
            Key = WorkspaceDefaultTimezone, Category = SettingCategories.General, Type = SettingValueType.String,
            Default = Json("UTC"), OwningService = SettingOwners.Workspace,
            MaxLength = 64, Pattern = TimezonePattern,
            Label = "New workspace time zone",
            Description = "The IANA time zone a new workspace starts with, e.g. Asia/Ho_Chi_Minh. Existing workspaces keep theirs.",
        },

        // Security & auth (edits need settings.security)
        new()
        {
            Key = AccessTokenMinutes, Category = SettingCategories.Security, Type = SettingValueType.Integer,
            Default = Json(30), OwningService = SettingOwners.Auth, Risky = true,
            Min = 5, Max = 240, Unit = "minutes",
            Label = "Access token lifetime",
            Description = "How long a signed-in session's access token lives. Applies to tokens issued after the change; a token cannot be recalled, so longer means a revoked account keeps access longer.",
        },
        new()
        {
            Key = RefreshTokenDays, Category = SettingCategories.Security, Type = SettingValueType.Integer,
            Default = Json(7), OwningService = SettingOwners.Auth, Risky = true,
            Min = 1, Max = 90, Unit = "days",
            Label = "Session lifetime (refresh token)",
            Description = "How long a user stays signed in without entering their password. Sets the refresh token and the session cookies together.",
        },
        new()
        {
            Key = PasswordMinLength, Category = SettingCategories.Security, Type = SettingValueType.Integer,
            Default = Json(6), OwningService = SettingOwners.Auth,
            Min = 6, Max = 64, Unit = "characters",
            Label = "Minimum password length",
            Description = "Checked on sign-up, password change and password reset. Existing passwords keep working.",
        },
        new()
        {
            Key = LockoutMaxFailedAttempts, Category = SettingCategories.Security, Type = SettingValueType.Integer,
            Default = Json(5), OwningService = SettingOwners.Auth,
            Min = 3, Max = 20, Unit = "attempts",
            Label = "Failed sign-ins before lockout",
            Description = "Consecutive wrong passwords that lock an account.",
        },
        new()
        {
            Key = LockoutDurationMinutes, Category = SettingCategories.Security, Type = SettingValueType.Integer,
            Default = Json(15), OwningService = SettingOwners.Auth,
            Min = 1, Max = 1440, Unit = "minutes",
            Label = "Lockout duration",
            Description = "How long a locked account stays locked. Staff can unlock one sooner from Accounts.",
        },
        new()
        {
            Key = GoogleSignInEnabled, Category = SettingCategories.Security, Type = SettingValueType.Boolean,
            Default = Json(true), OwningService = SettingOwners.Auth, Risky = true,
            Label = "Sign in with Google",
            Description = "Off refuses Google sign-in and sign-up and hides the button. Accounts created with Google can still reset a password and sign in with it.",
        },
        new()
        {
            Key = LoginRateLimit, Category = SettingCategories.Security, Type = SettingValueType.Integer,
            Default = Json(5), OwningService = SettingOwners.Gateway,
            Min = 1, Max = 100, Unit = "attempts per IP per window",
            Label = "Sign-in attempts per IP",
            Description = "Sign-in requests one IP address may make per rate-limit window before it gets 429.",
        },

        // Meetings & AI pipeline
        new()
        {
            Key = FlashModeDefault, Category = SettingCategories.Meetings, Type = SettingValueType.Boolean,
            Default = Json(true), OwningService = SettingOwners.AiStt, Risky = true,
            Label = "Flash mode default",
            Description = "Streaming transcription for rooms whose host has not chosen. The deployed STT model is tuned for flash mode; off is noticeably slower with it. A host's per-room choice still wins.",
        },
        new()
        {
            Key = ChunkDurationMs, Category = SettingCategories.Meetings, Type = SettingValueType.Integer,
            Default = Json(6000), OwningService = SettingOwners.AiStt,
            Min = 2000, Max = 15000, Unit = "ms",
            Label = "Maximum speech chunk",
            Description = "The longest stretch of speech sent to transcription as one piece. Shorter lowers caption delay and costs context; applies to microphones that start speaking after the change.",
        },
        new()
        {
            Key = SuggestMinWords, Category = SettingCategories.Meetings, Type = SettingValueType.Integer,
            Default = Json(4), OwningService = SettingOwners.AiSuggest,
            Min = 1, Max = 20, Unit = "words",
            Label = "AI suggest: minimum words",
            Description = "Utterances shorter than this never trigger a suggestion (questions have their own, lower bar).",
        },
        new()
        {
            Key = SuggestMinConfidence, Category = SettingCategories.Meetings, Type = SettingValueType.Decimal,
            Default = Json(0.55m), OwningService = SettingOwners.AiSuggest,
            Min = 0, Max = 1,
            Label = "AI suggest: minimum confidence",
            Description = "The model's own confidence a suggestion needs before it is shown.",
        },
        new()
        {
            Key = SuggestCooldownSeconds, Category = SettingCategories.Meetings, Type = SettingValueType.Integer,
            Default = Json(20), OwningService = SettingOwners.AiSuggest,
            Min = 0, Max = 600, Unit = "seconds",
            Label = "AI suggest: cooldown",
            Description = "Minimum gap between two suggestions in one meeting.",
        },
        new()
        {
            Key = SuggestMaxPerMeeting, Category = SettingCategories.Meetings, Type = SettingValueType.Integer,
            Default = Json(30), OwningService = SettingOwners.AiSuggest,
            Min = 0, Max = 200, Unit = "suggestions",
            Label = "AI suggest: per-meeting cap",
            Description = "No more suggestions after this many in one meeting. 0 turns suggestions off.",
        },
        new()
        {
            Key = SuggestMinSttConfidence, Category = SettingCategories.Meetings, Type = SettingValueType.Decimal,
            Default = Json(-0.5m), OwningService = SettingOwners.AiSuggest,
            Min = -5, Max = 0, Unit = "avg log-prob",
            Label = "AI suggest: minimum transcript confidence",
            Description = "Utterances transcribed less confidently than this (average log-probability) are ignored, so suggestions are not built on misheard speech.",
        },
        new()
        {
            Key = VoiceCloneMinSeconds, Category = SettingCategories.Meetings, Type = SettingValueType.Decimal,
            Default = Json(20m), OwningService = SettingOwners.AiTts,
            Min = 5, Max = 90, Unit = "seconds",
            Label = "Voice clone: sample length",
            Description = "Seconds of a speaker's audio collected before an in-meeting clone is attempted. Longer clones sound better and start later.",
        },
        new()
        {
            Key = VoiceCloneUpgradeMargin, Category = SettingCategories.Meetings, Type = SettingValueType.Decimal,
            Default = Json(0.15m), OwningService = SettingOwners.AiTts,
            Min = 0, Max = 1,
            Label = "Voice clone: upgrade margin",
            Description = "How much better (quality score) a later sample must be before an in-meeting clone is replaced by it.",
        },
        new()
        {
            Key = RecordingEnabled, Category = SettingCategories.Meetings, Type = SettingValueType.Boolean,
            Default = Json(true), OwningService = SettingOwners.Meeting, Risky = true,
            Label = "Meeting recording",
            Description = "Off refuses every new recording request. Recordings already running finish normally.",
        },

        // Billing
        new()
        {
            Key = TrialDays, Category = SettingCategories.Billing, Type = SettingValueType.Integer,
            Default = Json(14), OwningService = SettingOwners.Billing, Risky = true,
            Min = 1, Max = 90, Unit = "days",
            Label = "Trial length",
            Description = "Length of the trial a new workspace starts on. Running trials keep their end date.",
        },
        new()
        {
            Key = TrialCredits, Category = SettingCategories.Billing, Type = SettingValueType.Integer,
            Default = Json(20000), OwningService = SettingOwners.Billing, Risky = true,
            Min = 0, Max = 1_000_000, Unit = "credits",
            Label = "Trial credits",
            Description = "Credits a new trial starts with. Running trials keep their balance.",
        },
        new()
        {
            // Read by BillingService's SubscriptionExpirationWorker (dormancy sweep) and
            // CreditService.GetFrozenCreditsAsync (the date shown to the owner).
            Key = FrozenCreditGraceDays, Category = SettingCategories.Billing, Type = SettingValueType.Integer,
            Default = Json(30), OwningService = SettingOwners.Billing,
            Min = 0, Max = 3650, Unit = "days",
            Label = "Frozen credit grace window",
            Description = "Days an ended subscription's kept credits wait for a renewal before they are marked dormant. Dormant credits are never deleted and still come back on renewal.",
        },

        // Notifications & email
        new()
        {
            Key = EmailFromName, Category = SettingCategories.Notifications, Type = SettingValueType.String,
            Default = Json("WarpTalk"), OwningService = SettingOwners.Notification,
            MaxLength = 100, Pattern = @"^[^<>""@\r\n]+$",
            Label = "Sender name",
            Description = "The name transactional e-mail comes from (auth, workspace and notification e-mails).",
        },
        new()
        {
            Key = EmailFromAddress, Category = SettingCategories.Notifications, Type = SettingValueType.String,
            Default = Json("no-reply@warptalk.vn"), OwningService = SettingOwners.Notification, Risky = true,
            MaxLength = 254, Pattern = EmailPattern,
            Label = "Sender address",
            Description = "Must be on a domain verified with the e-mail provider (Resend); any other address is rejected by the provider and no e-mail is delivered.",
        },
        new()
        {
            Key = EmailReplyTo, Category = SettingCategories.Notifications, Type = SettingValueType.String,
            Default = Json(""), OwningService = SettingOwners.Notification,
            MaxLength = 254, Pattern = EmailPattern,
            Label = "Reply-to address",
            Description = "Where replies to transactional e-mail go. Empty sends none (replies go to the sender address).",
        },

        // Limits & quotas
        new()
        {
            Key = DocumentUploadMb, Category = SettingCategories.Limits, Type = SettingValueType.Integer,
            Default = Json(10), OwningService = SettingOwners.Workspace,
            Scopes = SettingScopes.Platform | SettingScopes.Plan | SettingScopes.Workspace,
            Min = 1, Max = 100, Unit = "MB",
            Label = "Knowledge document upload size",
            Description = "Largest file a workspace can upload to its knowledge base. Can be raised per plan or per workspace.",
        },
        new()
        {
            Key = MeetingCreatePerMinute, Category = SettingCategories.Limits, Type = SettingValueType.Integer,
            Default = Json(5), OwningService = SettingOwners.TranslationRoom,
            Scopes = SettingScopes.Platform | SettingScopes.Workspace,
            Min = 1, Max = 120, Unit = "meetings per minute",
            Label = "Meetings created per minute",
            Description = "How many meetings one workspace may create per minute. Raise it for a workspace that schedules in bulk.",
        },
        new()
        {
            Key = UserRateLimit, Category = SettingCategories.Limits, Type = SettingValueType.Integer,
            Default = Json(180), OwningService = SettingOwners.Gateway,
            Min = 30, Max = 5000, Unit = "requests per user per window",
            Label = "API requests per signed-in user",
            Description = "Requests one signed-in user may make per rate-limit window before 429. One page view is about ten requests.",
        },
        new()
        {
            Key = IpRateLimit, Category = SettingCategories.Limits, Type = SettingValueType.Integer,
            Default = Json(300), OwningService = SettingOwners.Gateway,
            Min = 30, Max = 5000, Unit = "requests per IP per window",
            Label = "API requests per anonymous IP",
            Description = "Requests one IP address may make without signing in, per rate-limit window, before 429.",
        },

        // Feature flags
        new()
        {
            Key = FlagAiSuggest, Category = SettingCategories.FeatureFlags, Type = SettingValueType.FeatureFlag,
            Default = FeatureFlagValue.On().ToJson(), OwningService = SettingOwners.AiSuggest,
            Label = "AI suggestions",
            Description = "In-meeting AI suggestions. The deployment must also enable the suggestion worker; this flag can only narrow it.",
        },
        new()
        {
            Key = FlagGlobalGlossary, Category = SettingCategories.FeatureFlags, Type = SettingValueType.FeatureFlag,
            Default = FeatureFlagValue.On().ToJson(), OwningService = SettingOwners.Transcript,
            Label = "Platform glossary in meetings",
            Description = "Adds the published platform glossary to transcription and translation prompts. A workspace can still opt out in its own AI policy.",
        },
        new()
        {
            Key = FlagWarpBotWebSearch, Category = SettingCategories.FeatureFlags, Type = SettingValueType.FeatureFlag,
            Default = FeatureFlagValue.On().ToJson(), OwningService = SettingOwners.AiAssistant,
            Label = "WarpBot web search",
            Description = "Lets WarpBot search the web. Off keeps it to the workspace's own meetings and documents.",
        },
        new()
        {
            Key = FlagVoiceClone, Category = SettingCategories.FeatureFlags, Type = SettingValueType.FeatureFlag,
            Default = FeatureFlagValue.On().ToJson(), OwningService = SettingOwners.AiTts,
            Label = "Voice cloning in meetings",
            Description = "Dubbing in the speaker's own (cloned) voice. Off dubs everyone in a standard voice; consent and the plan's entitlement are still required when on.",
        },
    ];

    private static readonly Dictionary<string, SettingDefinition> ByKey =
        All.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public static SettingDefinition? Find(string? key)
        => key is not null && ByKey.TryGetValue(key, out var definition) ? definition : null;

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);
}

namespace WarpTalk.AssistantService.Domain.Constants;

/// <summary>
/// The values <c>assistant.assistant_tool_calls</c> stores for each WarpBot tool call (wave 4).
/// </summary>
/// <remarks>
/// The worker (warptalk-ai <c>chat_worker.py</c>, <c>tool_call_log</c>) writes the same strings; the
/// web buckets plugin error codes into outcomes with the same rule
/// (<c>src/lib/assistant/plugin-activity.ts</c>).
/// </remarks>
public static class AssistantToolCallConstants
{
    public static class Sources
    {
        public const string Builtin = "builtin";
        public const string Plugin = "plugin";
        public const string WebSearch = "web_search";
    }

    public static class Outcomes
    {
        public const string Ok = "ok";
        public const string Error = "error";
        public const string Blocked = "blocked";
        public const string NeedsSetup = "needs_setup";
        public const string Declined = "declined";
        public const string ConfirmationRequired = "confirmation_required";
    }

    /// <summary>The worker's entry <c>status</c> for a call that finished.</summary>
    public const string CompletedStatus = "completed";

    /// <summary>Column widths, so an over-long value from the worker is cut rather than failing the insert.</summary>
    public const int ToolNameMaxLength = 100;
    public const int StatusMaxLength = 20;
    public const int SourceMaxLength = 20;
    public const int PluginKeyMaxLength = 100;
    public const int OutcomeMaxLength = 30;
    public const int OutcomeCodeMaxLength = 60;
}

namespace WarpTalk.TranslationRoomService.Domain.Enums;

public enum ArtifactStatus
{
    Active,
    Expired,
    Completed,

    /// <summary>
    /// rec-loss: a recording that has started and not yet ended. Written upper-case like
    /// <see cref="Completed"/>; the web folds status case-insensitively.
    /// </summary>
    Processing,

    /// <summary>
    /// rec-loss: a recording that ended without a file. The row is kept on purpose — it is what
    /// lets the record page say "the recording failed" instead of showing nothing at all.
    /// </summary>
    Failed
}

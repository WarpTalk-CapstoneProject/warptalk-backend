namespace WarpTalk.TranscriptService.Infrastructure.Redis;

public enum TranscriptResultStreamKind
{
    Unknown,
    Stt,
    Translation,
    Tts
}

public static class TranscriptConsumerPollingPolicy
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(250);

    public static IReadOnlyList<string> InputStreams { get; } =
        ["stt:results", "translate:results", "tts:results"];

    public static TimeSpan DelayAfterPass(int messagesRead) =>
        messagesRead == 0 ? IdleDelay : TimeSpan.Zero;

    public static TranscriptResultStreamKind Classify(string stream) =>
        stream switch
        {
            "stt:results" => TranscriptResultStreamKind.Stt,
            "translate:results" => TranscriptResultStreamKind.Translation,
            "tts:results" => TranscriptResultStreamKind.Tts,
            _ => TranscriptResultStreamKind.Unknown
        };
}

using System.Globalization;
using StackExchange.Redis;
using WarpTalk.Gateway.Services;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// Routing rules for the shared ai_assistant:results stream.
///
/// Summaries, action items and inline suggestions all arrive on one stream, and a wrong
/// answer here is silent in both directions: a suggestion leaking into the legacy branch
/// surfaces inside the meeting summary panel, and a summary mistaken for a suggestion
/// disappears from it.
/// </summary>
public class AiResultConsumerServiceTests
{
    private const string RoomId = "11111111-1111-1111-1111-111111111111";

    private static StreamEntry Entry(params (string Name, string Value)[] fields) =>
        new(
            "1-0",
            fields.Select(field => new NameValueEntry(field.Name, field.Value)).ToArray());

    private static StreamEntry SuggestionEntry(
        string segmentId = "segment-1",
        string content = "Chưa có ai nhận phần tích hợp.",
        string category = "action",
        string detail = "Deadline được nhắc tới nhưng thiếu owner.",
        string confidence = "0.82",
        string language = "vi") =>
        Entry(
            ("meeting_id", RoomId),
            ("segment_id", segmentId),
            ("category", category),
            ("content", content),
            ("type", "suggestion"),
            ("detail", detail),
            ("confidence", confidence),
            ("language", language),
            ("token_count", "150"),
            ("timestamp_ms", "1754006400000"));

    // ── Suggestions are recognised ────────────────────────────

    [Fact]
    public void TryReadSuggestion_MapsEveryField()
    {
        var suggestion = AiResultConsumerService.TryReadSuggestion(SuggestionEntry(), RoomId);

        Assert.NotNull(suggestion);
        Assert.Equal(RoomId, suggestion!.TranslationRoomId);
        Assert.Equal("segment-1", suggestion.SegmentId);
        Assert.Equal("action", suggestion.Category);
        Assert.Equal("Chưa có ai nhận phần tích hợp.", suggestion.Content);
        Assert.Equal("Deadline được nhắc tới nhưng thiếu owner.", suggestion.Detail);
        Assert.Equal(0.82f, suggestion.Confidence, 3);
        Assert.Equal("vi", suggestion.Language);
    }

    [Fact]
    public void TryReadSuggestion_TreatsBlankDetailAsAbsent()
    {
        var suggestion = AiResultConsumerService.TryReadSuggestion(
            SuggestionEntry(detail: "   "),
            RoomId);

        Assert.NotNull(suggestion);
        Assert.Null(suggestion!.Detail);
    }

    [Fact]
    public void TryReadSuggestion_DefaultsMissingConfidenceToZero()
    {
        var entry = Entry(
            ("meeting_id", RoomId),
            ("segment_id", "segment-1"),
            ("content", "x"),
            ("type", "suggestion"));

        var suggestion = AiResultConsumerService.TryReadSuggestion(entry, RoomId);

        Assert.NotNull(suggestion);
        Assert.Equal(0f, suggestion!.Confidence);
    }

    // ── Everything else falls through to the legacy branch ────

    [Theory]
    [InlineData("summary")]
    [InlineData("action_items")]
    [InlineData("some_future_type")]
    public void TryReadSuggestion_ReturnsNullForOtherTypes(string type)
    {
        var entry = Entry(
            ("meeting_id", RoomId),
            ("type", type),
            ("content", "Cuộc họp đã thống nhất..."));

        Assert.Null(AiResultConsumerService.TryReadSuggestion(entry, RoomId));
    }

    [Fact]
    public void TryReadSuggestion_ReturnsNullWhenTypeIsAbsent()
    {
        // The legacy branch defaults a missing type to "summary" — this must not intercept it.
        var entry = Entry(("meeting_id", RoomId), ("content", "Cuộc họp đã thống nhất..."));

        Assert.Null(AiResultConsumerService.TryReadSuggestion(entry, RoomId));
    }

    // ── Unrenderable suggestions are dropped, not downgraded ──

    [Fact]
    public void TryReadSuggestion_ReturnsNullWithoutASegmentToAnchorTo()
    {
        Assert.Null(
            AiResultConsumerService.TryReadSuggestion(SuggestionEntry(segmentId: ""), RoomId));
    }

    [Fact]
    public void TryReadSuggestion_ReturnsNullWithoutContent()
    {
        Assert.Null(
            AiResultConsumerService.TryReadSuggestion(SuggestionEntry(content: "   "), RoomId));
    }

    // ── STT confidence on the live caption path (WT-277) ──────

    [Fact]
    public void TryReadSttConfidence_IsNullWhenTheSegmentCarriesNoConfidence()
    {
        // WT-277: this used to default to 1.0f, so a segment the model reported nothing about was
        // pushed to every client looking maximally confident.
        var entry = Entry(("meeting_id", RoomId), ("segment_id", "segment-1"), ("text", "hello"));

        Assert.Null(AiResultConsumerService.TryReadSttConfidence(entry));
    }

    [Fact]
    public void TryReadSttConfidence_IsNullForTheSttWorkerUnknownSentinel()
    {
        // stt_worker/model.py's explicit "no logprobs on this event" value.
        var entry = Entry(("meeting_id", RoomId), ("confidence", "-1.0"));

        Assert.Null(AiResultConsumerService.TryReadSttConfidence(entry));
    }

    [Fact]
    public void TryReadSttConfidence_RoundTripsAGenuineMeasurement()
    {
        var entry = Entry(("meeting_id", RoomId), ("confidence", "-0.3421"));

        Assert.Equal(-0.3421f, AiResultConsumerService.TryReadSttConfidence(entry)!.Value, 4);
    }

    // ── Culture ───────────────────────────────────────────────

    [Fact]
    public void TryReadSuggestion_ParsesConfidenceIndependentlyOfHostCulture()
    {
        // The producer always writes "0.82". Under a comma-decimal culture, a
        // culture-sensitive parse reads the dot as a group separator and yields 82 —
        // turning the least confident hints into maximally confident ones.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("vi-VN");

            var suggestion = AiResultConsumerService.TryReadSuggestion(SuggestionEntry(), RoomId);

            Assert.NotNull(suggestion);
            Assert.Equal(0.82f, suggestion!.Confidence, 3);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}

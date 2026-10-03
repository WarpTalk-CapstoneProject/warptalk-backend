using WarpTalk.Gateway.Services;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// Live text (4 Oct 2026): stt_worker publishes the words of a turn still being spoken on
/// stt:interim, and the gateway relays them as TranscriptInterimReceived. These pin what the relay
/// will and will not route.
/// </summary>
public class InterimTranscriptRelayTests
{
    [Fact]
    public void A_live_text_message_is_read_field_by_field()
    {
        var parsed = AiResultConsumerService.TryReadInterim(
            """{"meeting_id":"room-1","speaker_id":"019f0d00-0de0-7000-9000-000000000003","item_id":"item_a","text":"Mọi người có câu hỏi gì","language":"vi"}""");

        Assert.NotNull(parsed);
        Assert.Equal("room-1", parsed!.Value.RoomId);
        Assert.Equal("item_a", parsed.Value.ItemId);
        Assert.Equal("Mọi người có câu hỏi gì", parsed.Value.Text);
        Assert.Equal("vi", parsed.Value.Language);
    }

    [Theory]
    [InlineData("""{"speaker_id":"s","text":"hello"}""")]
    [InlineData("""{"meeting_id":"room-1","text":"   "}""")]
    [InlineData("not json")]
    public void A_message_without_a_room_or_text_is_not_routed(string json)
    {
        Assert.Null(AiResultConsumerService.TryReadInterim(json));
    }
}

using WarpTalk.MeetingService.Application.Helpers;
using Xunit;

namespace WarpTalk.MeetingService.Tests.Helpers;

/// <summary>
/// WarpBot's answers carry a machine-readable note of the meeting they created, which the browser
/// turns into a card. Anything that treats the answer as prose has to take it out first.
/// </summary>
public class MeetingChatMarkersTests
{
    private const string Marker =
        "<!-- warpbot:meeting {\"kind\":\"google_meet\",\"url\":\"https://meet.google.com/abc-defg-hij\"} -->";

    [Fact]
    public void TheMarkerNeverReachesTheTranslator()
    {
        var answer = $"Đã tạo cuộc họp Google Meet.\n\n{Marker}";

        Assert.Equal("Đã tạo cuộc họp Google Meet.", MeetingChatMarkers.WithoutMeetingMarkers(answer));
    }

    [Fact]
    public void ProseAroundTheMarkerIsKept()
    {
        var answer = $"Trước.\n\n{Marker}\n\nSau.";

        Assert.Equal("Trước.\n\nSau.", MeetingChatMarkers.WithoutMeetingMarkers(answer));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("An ordinary message.")]
    public void AnythingWithoutAMarkerIsUntouched(string? text)
    {
        Assert.Equal(text ?? string.Empty, MeetingChatMarkers.WithoutMeetingMarkers(text));
    }
}

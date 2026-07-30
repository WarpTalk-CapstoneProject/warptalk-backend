using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using WarpTalk.MeetingService.API.Controllers;

namespace WarpTalk.MeetingService.Tests.API.Controllers;

public class MeetingChatControllerRouteTests
{
    [Fact]
    public void ControllerRoute_MatchesFrontendMeetingsRoomsContract()
    {
        var route = typeof(MeetingChatController)
            .GetCustomAttribute<RouteAttribute>();

        Assert.NotNull(route);
        Assert.Equal("api/v1/meetings/rooms/{roomId:guid}/chat", route!.Template);
    }

    [Theory]
    [InlineData(nameof(MeetingChatController.GetMessages), "GET", null)]
    [InlineData(nameof(MeetingChatController.SendMessage), "POST", null)]
    [InlineData(nameof(MeetingChatController.RequestTranslation), "POST", "{messageId:guid}/translate")]
    [InlineData(nameof(MeetingChatController.ModerateMessage), "POST", "{messageId:guid}/moderate")]
    public void ChatActions_ExposeFrontendExpectedHttpContracts(string actionName, string method, string? template)
    {
        var action = typeof(MeetingChatController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(methodInfo => methodInfo.Name == actionName);

        var httpMethod = action.GetCustomAttributes<HttpMethodAttribute>().Single();

        Assert.Equal(method, httpMethod.HttpMethods.Single());
        Assert.Equal(template, httpMethod.Template);
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.MeetingService.API.Controllers;
using WarpTalk.MeetingService.Application.DTOs;

namespace WarpTalk.MeetingService.Tests.API.Controllers;

/// <summary>
/// The browser's account of its LiveKit connection. Log-only, so the properties worth pinning are
/// the ones that keep a client from writing whatever it likes into the log.
/// </summary>
public sealed class MeetingClientEventsControllerTests
{
    private readonly Mock<ILogger<MeetingClientEventsController>> _logger = new();

    private MeetingClientEventsController Create(bool authenticated = true)
    {
        var identity = authenticated
            ? new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test")
            : new ClaimsIdentity();
        return new MeetingClientEventsController(_logger.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
            },
        };
    }

    private string LoggedMessage()
    {
        var call = _logger.Invocations.Single(i => i.Method.Name == nameof(ILogger.Log));
        return call.Arguments[2]!.ToString()!;
    }

    [Fact]
    public void A_known_event_is_logged_and_answered_with_no_content()
    {
        var result = Create().Report(Guid.NewGuid(), new MeetingClientEventRequest("connect_error", "NotAllowed", "could not establish pc connection", 15000, 1));

        Assert.IsType<NoContentResult>(result);
        Assert.Contains("connect_error", LoggedMessage());
        Assert.Contains("could not establish pc connection", LoggedMessage());
    }

    [Fact]
    public void An_unknown_kind_is_refused_and_not_logged()
    {
        var result = Create().Report(Guid.NewGuid(), new MeetingClientEventRequest("anything_at_all"));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.DoesNotContain(_logger.Invocations, i => i.Method.Name == nameof(ILogger.Log));
    }

    [Fact]
    public void A_caller_cannot_forge_extra_log_lines_or_flood_one()
    {
        var hostile = "first\nFAKE: Meeting client event connected" + new string('x', 5000);

        Create().Report(Guid.NewGuid(), new MeetingClientEventRequest("disconnected", Message: hostile));

        var message = LoggedMessage();
        Assert.DoesNotContain('\n', message);
        Assert.True(message.Length < 1500, $"logged {message.Length} chars");
    }

    [Fact]
    public void An_anonymous_caller_is_refused()
    {
        Assert.IsType<UnauthorizedObjectResult>(
            Create(authenticated: false).Report(Guid.NewGuid(), new MeetingClientEventRequest("connected")));
    }
}

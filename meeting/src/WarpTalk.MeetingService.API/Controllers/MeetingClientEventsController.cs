using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.MeetingService.API.Controllers;

/// <summary>
/// A browser's account of its own LiveKit connection, written to this service's log.
///
/// WHY IT EXISTS
///     Room 01a100d8, 3 Oct 2026: a participant held a valid token for 43 seconds and LiveKit never
///     saw them join, while another participant who joined 24 seconds later was in within two. The
///     server side could rule out every cause it owns — waiting room, displacement, the token — and
///     could not say what did happen, because the only record of a failed `room.connect` was a
///     message on that person's screen. They reloaded, and the evidence went with the tab.
///
/// LOG ONLY. Nothing is stored and nothing reacts to these: a client can say anything here, so
///     nothing may be decided on it. Every string is bounded and the kind is an allow-list, so a
///     caller cannot write an arbitrary line into the log.
/// </summary>
[ApiController]
[Route("api/v1/meetings")]
[Authorize]
public class MeetingClientEventsController : ControllerBase
{
    private const int MaxTextLength = 300;

    /// <summary>The events the meeting client sends; anything else is refused.</summary>
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "connect_error",
        "connect_slow",
        "connect_retry",
        "connected",
        "disconnected",
    };

    private readonly ILogger<MeetingClientEventsController> _logger;

    public MeetingClientEventsController(ILogger<MeetingClientEventsController> logger)
    {
        _logger = logger;
    }

    [HttpPost("rooms/{translationRoomId}/client-events")]
    public IActionResult Report(Guid translationRoomId, [FromBody] MeetingClientEventRequest? request)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        if (request == null || string.IsNullOrWhiteSpace(request.Kind) || !Kinds.Contains(request.Kind))
            return BadRequest(new ApiErrorResponse("Unknown client event kind.", ErrorCodes.ValidationError));

        // Warning for the ones somebody should look at; information for a successful connect,
        // which is only interesting for how long it took.
        var level = request.Kind == "connected" ? LogLevel.Information : LogLevel.Warning;
        _logger.Log(
            level,
            "Meeting client event {Kind} in room {RoomId} from user {UserId}: code={Code} elapsedMs={ElapsedMs} attempt={Attempt} message={Message} userAgent={UserAgent}",
            request.Kind,
            translationRoomId,
            userId.Value,
            Bound(request.Code),
            request.ElapsedMs,
            request.Attempt,
            Bound(request.Message),
            Bound(request.UserAgent));

        return NoContent();
    }

    private static string? Bound(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        // Newlines would let one report forge several log lines.
        var flat = value.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= MaxTextLength ? flat : flat[..MaxTextLength];
    }
}

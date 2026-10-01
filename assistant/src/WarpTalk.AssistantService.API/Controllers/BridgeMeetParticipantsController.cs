using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AssistantService.API.Controllers;

/// <summary>
/// The Google Meet side of a bridge room, read through the host's Google grant. Lives here rather
/// than in TranslationRoomService because this service holds the grant; the gateway routes this
/// one path under translation-rooms to it.
/// </summary>
[ApiController]
[Route("api/v1/translation-rooms/{roomId:guid}/bridge")]
[Authorize]
public class BridgeMeetParticipantsController : ControllerBase
{
    private readonly IBridgeMeetRosterService _roster;

    public BridgeMeetParticipantsController(IBridgeMeetRosterService roster)
    {
        _roster = roster;
    }

    /// <summary>
    /// Who is in the bridged Google Meet (the live conference, else the most recent one), for the
    /// host's far-side labelling. Host and the room's participants only.
    /// </summary>
    /// <response code="409">
    /// <c>meet_scope_missing</c> — the host has not granted Google Meet conference access
    /// (<c>connection_required</c> when the host has no Google connection at all).
    /// </response>
    [HttpGet("meet-participants")]
    [ProducesResponseType(typeof(IReadOnlyList<MeetParticipantResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetMeetParticipants(Guid roomId, CancellationToken ct)
    {
        var callerId = User.GetUserId();
        if (callerId is null) return Unauthorized();

        var result = await _roster.GetAsync(roomId, callerId.Value, ct);
        if (result.IsSuccess)
            return Ok(result.Value!.Select(MeetParticipantResponse.From).ToList());

        var body = new { error = result.Error, errorCode = result.ErrorCode };
        return result.ErrorCode switch
        {
            BridgeMeetRosterService.ErrorCodes.RoomNotFound => NotFound(body),
            BridgeMeetRosterService.ErrorCodes.Forbidden => StatusCode(StatusCodes.Status403Forbidden, body),
            BridgeMeetRosterService.ErrorCodes.NotABridgeRoom => Conflict(body),
            MeetConferenceErrorCodes.MeetScopeMissing => Conflict(body),
            MeetConferenceErrorCodes.ConnectionRequired => Conflict(body),
            MeetConferenceErrorCodes.PermissionDenied => Conflict(body),
            MeetConferenceErrorCodes.InvalidMeeting => Conflict(body),
            BridgeMeetRosterService.ErrorCodes.RoomUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, body),
            _ => StatusCode(StatusCodes.Status502BadGateway, body),
        };
    }
}

/// <summary>One person in the bridged Google Meet.</summary>
/// <param name="Key">The Meet participant resource name; stable for the conference.</param>
/// <param name="Kind">signedin | anonymous | phone.</param>
public sealed record MeetParticipantResponse(
    string Key,
    string DisplayName,
    string Kind,
    DateTimeOffset? JoinedAt,
    DateTimeOffset? LeftAt)
{
    public static MeetParticipantResponse From(MeetParticipantDto dto) =>
        new(dto.Key, dto.DisplayName, dto.Kind, dto.JoinedAt, dto.LeftAt);
}

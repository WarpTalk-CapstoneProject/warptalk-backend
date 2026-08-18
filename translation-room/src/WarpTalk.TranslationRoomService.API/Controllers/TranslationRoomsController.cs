using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;
using FluentValidation;
using WarpTalk.Shared;
using WarpTalk.Shared.Models;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.TranslationRoomService.API.Controllers;

[ApiController]
[Route("api/v1/translation-rooms")]
[Authorize]
public class TranslationRoomsController : ControllerBase
{
    private readonly ITranslationRoomService _translationRoomService;
    private readonly ITranslationRoomArtifactService _artifactService;
    private readonly ITranslationRoomSeriesService _seriesService;

    public TranslationRoomsController(
        ITranslationRoomService translationRoomService,
        ITranslationRoomArtifactService artifactService,
        ITranslationRoomSeriesService seriesService)
    {
        _translationRoomService = translationRoomService;
        _artifactService = artifactService;
        _seriesService = seriesService;
    }

    [HttpGet]
    public async Task<IActionResult> GetTranslationRooms([FromQuery] GetTranslationRoomsRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.GetTranslationRoomsAsync(request, userId.Value, User.GetEmail(), ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value!);
    }

    [HttpPost]

    public async Task<IActionResult> CreateTranslationRoom([FromBody] CreateTranslationRoomRequest request)
    {
        var hostId = User.GetUserId();
        if (hostId == null)
        {
            return Unauthorized();
        }

        if (!User.IsEmailVerified())
        {
            return StatusCode(403, new ApiErrorResponse("Email not verified", ErrorCodes.AccountPending));
        }

        // WT-327: a request carrying a recurrence rule is a BOOKING, not a meeting. It goes to
        // the series service, which materialises the occurrences inside the current horizon and
        // hands back the first one — so the client's happy path is unchanged: it still gets a
        // room with an id and a code to show and share.
        if (request.Recurrence is not null)
        {
            var seriesResult = await _seriesService.CreateSeriesAsync(request, hostId.Value, HttpContext.RequestAborted);
            if (!seriesResult.IsSuccess)
            {
                return seriesResult.ErrorCode switch
                {
                    ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(seriesResult.Error, seriesResult.ErrorCode)),
                    ErrorCodes.ServiceUnavailable => StatusCode(503, new ApiErrorResponse(seriesResult.Error, seriesResult.ErrorCode)),
                    _ => BadRequest(new ApiErrorResponse(seriesResult.Error, seriesResult.ErrorCode)),
                };
            }

            return CreatedAtAction(
                nameof(CreateTranslationRoom),
                new { id = seriesResult.Value!.FirstOccurrence.Id },
                seriesResult.Value);
        }

        var result = await _translationRoomService.CreateTranslationRoomAsync(request, hostId.Value);

        if (!result.IsSuccess)
        {
            // WT-249: a revoked host permission is not a malformed request — the client shows a
            // different message for "you may not" than for "we could not check right now".
            return result.ErrorCode switch
            {
                ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
                ErrorCodes.ServiceUnavailable => StatusCode(503, new ApiErrorResponse(result.Error, result.ErrorCode)),
                _ => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            };
        }

        return CreatedAtAction(nameof(CreateTranslationRoom), new { id = result.Value!.Id }, result.Value);
    }

    /// <summary>
    /// WT-334: now passes the caller, like every sibling read on this controller already did
    /// (<see cref="GetTranslationRooms"/>, and the invitation/artifact/feedback reads below). This
    /// one did not, and the service method it called took no user id, so <c>[Authorize]</c> alone
    /// let any authenticated user read any room across every tenant.
    ///
    /// Still returns NotFound for a refusal — the service returns the same not-found Result for
    /// "no such room" and "not yours" on purpose, so this mapping stays a single branch and cannot
    /// grow a 403 that re-confirms the room's existence.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetTranslationRoom(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.GetTranslationRoomAsync(id, userId.Value, User.GetEmail(), ct);
        if (!result.IsSuccess)
            return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value!);
    }

    [HttpPost("join")]
    public async Task<IActionResult> JoinTranslationRoom([FromBody] JoinTranslationRoomRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var userEmail = User.FindFirstValue(ClaimTypes.Email);

        var result = await _translationRoomService.JoinTranslationRoomAsync(request, userId.Value, userEmail, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value!);
    }

    /// <summary>
    /// WT-468: the languages the pre-join screen may offer for this room code.
    ///
    /// The rule is the room OWNER's: a joiner sees what the workspace that owns the room permits,
    /// not what their own currently-selected workspace permits. The screen could not apply that
    /// rule before because it holds a code and nothing else, so it read the joiner's own workspace
    /// settings — and someone in workspace A joining a room in workspace B got A's language list.
    ///
    /// Always 200. An unknown or half-typed code answers with empty lists, which every consumer
    /// reads as "no restriction", because this is called while the user is still typing. It is
    /// therefore not a room-existence probe either. The join endpoint remains the one place a bad
    /// code is reported.
    ///
    /// WT-490 added <c>roomLanguages</c>: the set the ROOM declares. The workspace policy alone was
    /// never enough — a workspace permitting four languages and a room declaring two offered four,
    /// so a joiner could pick a language nobody in the room would speak. The two limits are sent
    /// separately and intersected by the caller, because an empty list has to keep meaning
    /// "unrestricted from this source" rather than "offer nothing".
    /// </summary>
    /// <summary>
    /// WT-480: share this meeting's record with the people who took part, or take it back.
    ///
    /// Writes the room's <c>ArtifactAccess</c> policy, which already governs the transcript, the AI
    /// summary and the recording together — so this one control shares all three, and the button
    /// that calls it says so.
    ///
    /// Its own route rather than a field on the settings PUT, because that endpoint refuses any
    /// room past WAITING and this act only makes sense after the meeting has ended.
    /// </summary>
    [HttpPut("{id:guid}/artifact-access")]
    public async Task<IActionResult> SetArtifactAccess(Guid id, [FromBody] SetArtifactAccessRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.SetArtifactAccessAsync(id, userId.Value, request.Level, ct);
        if (result.IsSuccess)
            return NoContent();

        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Unauthorized => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
        };
    }

    [HttpGet("join-language-policy/{code}")]
    public async Task<IActionResult> GetJoinLanguagePolicy(string code, CancellationToken ct)
    {
        var result = await _translationRoomService.GetJoinLanguagePolicyByCodeAsync(code, ct);
        // Spelled out rather than returning the record, so the wire names are pinned here: the
        // pre-join screen reads exactly these two keys and `allowedTargetLanguages` predates this.
        return Ok(new
        {
            allowedTargetLanguages = result.Value?.AllowedTargetLanguages ?? Array.Empty<string>(),
            roomLanguages = result.Value?.RoomLanguages ?? Array.Empty<string>(),
        });
    }

    /// <summary>
    /// WT-433 (Linear): join by room id — what a shared LINK produces. A workspace member who was
    /// never invited used to dead-end on "Room information is unavailable" (the detail read
    /// correctly refuses them); this endpoint is their path into the waiting room instead.
    /// </summary>
    [HttpPost("{id:guid}/join")]
    public async Task<IActionResult> JoinTranslationRoomById(Guid id, [FromBody] JoinTranslationRoomRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var userEmail = User.FindFirstValue(ClaimTypes.Email);

        var result = await _translationRoomService.JoinTranslationRoomByIdAsync(id, request, userId.Value, userEmail, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Conflict)
                return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value!);
    }

    [HttpPost("{id}/waiting")]
    public async Task<IActionResult> OpenWaitingRoom(Guid id, CancellationToken ct)
    {
        var hostId = User.GetUserId();
        if (hostId == null) return Unauthorized();

        var result = await _translationRoomService.OpenWaitingRoomAsync(id, hostId.Value, ct);
        if (!result.IsSuccess) return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return NoContent();
    }



    [HttpPost("{id}/pause")]
    public async Task<IActionResult> PauseTranslationRoom(Guid id, CancellationToken ct)
    {
        var hostId = User.GetUserId();
        if (hostId == null) return Unauthorized();

        var result = await _translationRoomService.PauseTranslationRoomAsync(id, hostId.Value, ct);
        if (!result.IsSuccess) return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return NoContent();
    }

    /// <summary>
    /// Stop translating; keep the meeting (and therefore the transcript) running. The counterpart
    /// to <see cref="ResumeTranslationRoom"/>, which is Start Translation.
    /// </summary>
    [HttpPost("{id}/translation/stop")]
    public async Task<IActionResult> StopTranslation(Guid id, CancellationToken ct)
    {
        var hostId = User.GetUserId();
        if (hostId == null) return Unauthorized();

        var result = await _translationRoomService.StopTranslationAsync(id, hostId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Unauthorized) return Unauthorized(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.InvalidState) return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return NoContent();
    }

    [HttpPost("{id}/resume")]
    public async Task<IActionResult> ResumeTranslationRoom(Guid id, CancellationToken ct)
    {
        var hostId = User.GetUserId();
        if (hostId == null) return Unauthorized();

        var result = await _translationRoomService.ResumeTranslationRoomAsync(id, hostId.Value, ct);
        if (!result.IsSuccess) return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return NoContent();
    }



    [HttpPost("{id}/end")]
    public async Task<IActionResult> EndTranslationRoom(Guid id, CancellationToken ct)
    {
        var hostId = User.GetUserId();
        if (hostId == null)
            return Unauthorized();

        var result = await _translationRoomService.EndTranslationRoomAsync(id, hostId.Value, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return NoContent();
    }

    [HttpPost("{id}/start")]
    public async Task<IActionResult> StartTranslationRoom(Guid id, CancellationToken ct)
    {
        // WT-341: no longer necessarily the host. Entitlement to start is resolved in the service,
        // against the room's own RequiresApproval setting — the email claim is needed because an
        // invitee is identified by email, not by a participant row they may not have yet.
        var callerId = User.GetUserId();
        if (callerId == null)
            return Unauthorized();

        var result = await _translationRoomService.StartTranslationRoomAsync(id, callerId.Value, User.GetEmail(), ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden) return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Unauthorized) return Unauthorized(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.InvalidState) return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value!);
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> CancelTranslationRoom(Guid id, CancellationToken ct)
    {
        var hostId = User.GetUserId();
        if (hostId == null)
            return Unauthorized();

        var result = await _translationRoomService.CancelTranslationRoomAsync(id, hostId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden) return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Unauthorized) return Unauthorized(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.InvalidState) return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value!);
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetTranslationRoomHistory([FromQuery] GetTranslationRoomsRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.GetTranslationRoomHistoryAsync(request, userId.Value, User.GetEmail(), ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden) return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Unauthorized) return Unauthorized(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.InvalidState) return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value!);
    }

    /// <summary>
    /// WT-333 — UC 25. The caller's own meetings in one workspace, past and upcoming together.
    ///
    /// Separate action rather than a <c>?scope=mine</c> flag on <c>history</c> because the two
    /// answer different questions and default differently (this one carries no status filter and
    /// orders by the booked slot). A <c>scope</c> sent to this route is ignored — the service pins
    /// it — so no caller can widen a personal read back to the whole tenant by guessing a value.
    /// </summary>
    [HttpGet("my-meetings")]
    public async Task<IActionResult> GetMyMeetings([FromQuery] GetTranslationRoomsRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.GetMyMeetingsAsync(request, userId.Value, User.GetEmail(), ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden) return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Unauthorized) return Unauthorized(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.InvalidState) return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value!);
    }

    [HttpGet("{id}/artifacts")]
    public async Task<IActionResult> GetTranslationRoomArtifacts(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.GetTranslationRoomArtifactsAsync(id, userId.Value, User.GetEmail(), ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden) return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Unauthorized) return Unauthorized(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.InvalidState) return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value!);
    }

    [HttpGet("{id}/invitations")]
    public async Task<IActionResult> GetTranslationRoomInvitations(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.GetTranslationRoomInvitationsAsync(id, userId.Value, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden) return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value!);
    }

    /// <summary>
    /// The invitee accepts their own invitation. Not a join: the meeting is usually still ahead,
    /// and this is the only action the invitation notification can offer when it arrives.
    /// </summary>
    [HttpPost("{id}/invitations/accept")]
    public async Task<IActionResult> AcceptTranslationRoomInvitation(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.AcceptTranslationRoomInvitationAsync(
            id, userId.Value, User.GetEmail(), ct);

        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.InvalidState) return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value!);
    }

    [HttpGet("{id}/feedback/me")]
    public async Task<IActionResult> GetMyFeedback(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.GetFeedbackStateAsync(id, userId.Value, User.GetEmail(), ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden) return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Unauthorized) return Unauthorized(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.InvalidState) return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value!);
    }

    [HttpPost("{id}/feedback")]
    public async Task<IActionResult> SubmitFeedback(Guid id, [FromBody] SubmitTranslationRoomFeedbackRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.SubmitFeedbackAsync(id, userId.Value, request, User.GetEmail(), ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Forbidden) return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Unauthorized) return Unauthorized(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.InvalidState) return Conflict(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return CreatedAtAction(nameof(GetMyFeedback), new { id }, result.Value!);
    }
    // WT-14: plain downloadable/emailable link, so [Authorize] here also accepts a JWT
    // via the "access_token" query string (see Program.cs JwtBearerEvents.OnMessageReceived) —
    // a calendar app or browser tab opening this URL directly can't attach an Authorization header.
    [HttpGet("{id}/calendar.ics")]
    public async Task<IActionResult> DownloadCalendarIcs(Guid id, CancellationToken ct)
    {
        var result = await _translationRoomService.GenerateCalendarIcsAsync(id, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(result.Value!);
        return File(bytes, "text/calendar", "meeting.ics");
    }

    //Chua co enpoint PATCH nen tach rieng settings
    [HttpPut("{id}/settings")]
    public async Task<IActionResult> UpdateTranslationRoomSettings(Guid id, [FromBody] UpdateRoomSettingsRequest request, CancellationToken ct)
    {
        var hostId = User.GetUserId();
        if (hostId == null)
            return Unauthorized();

        var result = await _translationRoomService.UpdateTranslationRoomSettingsAsync(id, hostId.Value, request, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return NoContent();
    }


}

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
    private readonly WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceClient _workspaceClient;
    private readonly ILogger<TranslationRoomsController> _logger;

    public TranslationRoomsController(
        ITranslationRoomService translationRoomService,
        ITranslationRoomArtifactService artifactService,
        WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceClient workspaceClient,
        ILogger<TranslationRoomsController> logger)
    {
        _translationRoomService = translationRoomService;
        _artifactService = artifactService;
        _workspaceClient = workspaceClient;
        _logger = logger;
    }


    [AllowAnonymous]
    [HttpGet("preflight/{roomCode}")]
    public async Task<IActionResult> GetRoomPreflight(string roomCode, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(roomCode))
            {
                return NotFound(new ApiErrorResponse("Translation room not found or unavailable.", ErrorCodes.NotFound));
            }

            // 1. Resolve room from TranslationRoom service
            var roomResult = await _translationRoomService.GetTranslationRoomByCodeAsync(roomCode, ct);
            if (!roomResult.IsSuccess || roomResult.Value == null)
            {
                // [Security] Return generic 404 to avoid code enumeration
                return NotFound(new ApiErrorResponse("Translation room not found or unavailable.", ErrorCodes.NotFound));
            }

            var room = roomResult.Value;

            // 2. Fetch workspace preflight details from Workspace service via gRPC
            var userEmail = User.FindFirstValue(ClaimTypes.Email);
            var workspacePreflightRequest = new WarpTalk.Shared.Protos.GetWorkspacePreflightRequest
            {
                WorkspaceId = room.WorkspaceId.ToString(),
                UserEmail = userEmail ?? string.Empty
            };

            WarpTalk.Shared.Protos.GetWorkspacePreflightResponse workspaceDetails;
            try
            {
                workspaceDetails = await _workspaceClient.GetWorkspacePreflightDetailsAsync(workspacePreflightRequest, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to call GetWorkspacePreflightDetails via gRPC for WorkspaceId {WorkspaceId}", room.WorkspaceId);
                return NotFound(new ApiErrorResponse("Translation room not found or unavailable.", ErrorCodes.NotFound));
            }

            if (!workspaceDetails.IsActive)
            {
                // [Security] Return generic 404 to avoid enumeration of rooms in inactive/deleted workspaces
                return NotFound(new ApiErrorResponse("Translation room not found or unavailable.", ErrorCodes.NotFound));
            }

            // 3. Verify user membership in Workspace via gRPC
            bool isUserMember = false;
            var userIdStr = User.GetUserId()?.ToString();
            if (!string.IsNullOrEmpty(userIdStr))
            {
                try
                {
                    var memberDetails = await _workspaceClient.GetWorkspaceMemberDetailsAsync(
                        new WarpTalk.Shared.Protos.GetWorkspaceMemberRequest
                        {
                            WorkspaceId = room.WorkspaceId.ToString(),
                            UserId = userIdStr
                        },
                        cancellationToken: ct);
                    isUserMember = memberDetails.IsMember;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to call GetWorkspaceMemberDetails via gRPC for user {UserId}", userIdStr);
                }
            }

            // 4. Check if join request is required (if user is authenticated but not a member)
            bool isAuthenticated = !string.IsNullOrEmpty(userIdStr);
            bool requiresJoinRequest = isAuthenticated && !isUserMember;

            // 5. Expose workspace name and slug ONLY if user is member, domain is matched, or external collaboration allowed
            bool isDomainMatched = workspaceDetails.IsDomainMatched;
            bool allowExternalCollaboration = workspaceDetails.AllowExternalCollaboration;

            bool canExposeWorkspaceInfo = isUserMember || isDomainMatched || allowExternalCollaboration;

            string? workspaceName = canExposeWorkspaceInfo ? workspaceDetails.WorkspaceName : null;
            string? workspaceSlug = canExposeWorkspaceInfo ? workspaceDetails.WorkspaceSlug : null;

            var response = new RoomPreflightResponse(
                RoomCode: roomCode,
                RequiresJoinRequest: requiresJoinRequest,
                IsUserMember: isUserMember,
                IsDomainMatched: isDomainMatched,
                AllowExternalCollaboration: allowExternalCollaboration,
                WorkspaceName: workspaceName,
                WorkspaceSlug: workspaceSlug,
                IsAuthenticated: isAuthenticated
            );

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during room preflight for room code {RoomCode}", roomCode);
            return NotFound(new ApiErrorResponse("Translation room not found or unavailable.", ErrorCodes.NotFound));
        }
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

        var result = await _translationRoomService.CreateTranslationRoomAsync(request, hostId.Value);

        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return CreatedAtAction(nameof(CreateTranslationRoom), new { id = result.Value!.Id }, result.Value);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetTranslationRoom(Guid id, CancellationToken ct)
    {
        var result = await _translationRoomService.GetTranslationRoomAsync(id, ct);
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
        var hostId = User.GetUserId();
        if (hostId == null)
            return Unauthorized();

        var result = await _translationRoomService.StartTranslationRoomAsync(id, hostId.Value, ct);
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

    [HttpGet("{id}/artifacts")]
    public async Task<IActionResult> GetTranslationRoomArtifacts(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.GetTranslationRoomArtifactsAsync(id, userId.Value, ct);
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

    [HttpGet("{id}/feedback/me")]
    public async Task<IActionResult> GetMyFeedback(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.GetFeedbackStateAsync(id, userId.Value, ct);
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

        var result = await _translationRoomService.SubmitFeedbackAsync(id, userId.Value, request, ct);
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

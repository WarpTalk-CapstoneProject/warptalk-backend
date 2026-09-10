using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.Models;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Controllers;

/// <summary>
/// Biên bản họp — the signed meeting record.
///
/// Separate from RoomArtifactsController because a minutes document is not an artifact: artifacts
/// are outputs a job produced, and this has a lifecycle, an owner and a signature.
/// </summary>
[ApiController]
[Route("api/v1/rooms/{roomId}/minutes")]
[Authorize]
public class MeetingMinutesController : ControllerBase
{
    private readonly IMeetingMinutesService _minutesService;

    public MeetingMinutesController(IMeetingMinutesService minutesService)
    {
        _minutesService = minutesService;
    }

    /// <summary>
    /// This meeting's proceedings in a language the document does not already carry.
    ///
    /// A GET that can cause work, for the same reason the summary's is: the alternative is three
    /// calls to answer "show me this record in Japanese", and the work is cached and bounded by
    /// the languages a room's readers actually ask for. It writes nothing to the document — the
    /// biên bản, its number and its signatures are untouched.
    ///
    /// 200 with `status: "unavailable"` and a reason is a real answer, not an error: a document
    /// its secretary has edited cannot honestly be translated from the summary, and a reader is
    /// owed that sentence rather than an empty picker.
    /// </summary>
    [HttpGet("translation")]
    public async Task<IActionResult> GetTranslation(
        Guid roomId,
        [FromQuery] string language,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        // Forwarded so that, if this has to generate, the summary is read as this caller through
        // the endpoint they could already use — never a privileged bypass.
        var bearerToken = Request.Headers["Authorization"].ToString();

        return Respond<MinutesTranslationDto>(await _minutesService.GetTranslationAsync(
            roomId, userId.Value, User.GetEmail(), language, bearerToken, ct));
    }

    [HttpGet]
    public async Task<IActionResult> GetCurrent(Guid roomId, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        return Respond(await _minutesService.GetCurrentAsync(roomId, userId.Value, User.GetEmail(), ct));
    }

    /// <summary>Draw up the draft. Idempotent while one is still unapproved.</summary>
    [HttpPost("draft")]
    public async Task<IActionResult> CreateDraft(Guid roomId, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        return Respond(await _minutesService.CreateDraftAsync(roomId, userId.Value, ct));
    }

    [HttpPut("{minutesId}")]
    public async Task<IActionResult> UpdateContent(
        Guid roomId,
        Guid minutesId,
        [FromBody] UpdateMinutesContentRequest request,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request?.Content))
        {
            return BadRequest(new ApiErrorResponse("Minutes content is required.", ErrorCodes.ValidationError));
        }

        return Respond(await _minutesService.UpdateContentAsync(
            roomId, minutesId, userId.Value, request.Content, ct));
    }

    [HttpPost("{minutesId}/sign")]
    public async Task<IActionResult> Sign(Guid roomId, Guid minutesId, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        return Respond(await _minutesService.SignAsync(roomId, minutesId, userId.Value, ct));
    }

    [HttpPost("{minutesId}/approve")]
    public async Task<IActionResult> Approve(Guid roomId, Guid minutesId, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        return Respond(await _minutesService.ApproveAsync(roomId, minutesId, userId.Value, ct));
    }

    [HttpPost("{minutesId}/revise")]
    public async Task<IActionResult> Revise(Guid roomId, Guid minutesId, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        return Respond(await _minutesService.ReviseAsync(roomId, minutesId, userId.Value, ct));
    }

    /// <summary>
    /// Download the minutes as a Word document.
    ///
    /// <paramref name="template"/> selects the layout — "global-en" (the default) or "vn-nd30".
    /// It is a query parameter rather than a stored preference because the choice belongs to the
    /// person sending the document, and one meeting's minutes routinely goes to both a domestic
    /// filing and a foreign partner. An unrecognised value renders the default rather than
    /// failing: see MinutesTemplates.Normalise.
    /// </summary>
    [HttpGet("export.docx")]
    public Task<IActionResult> ExportDocx(
        Guid roomId, [FromQuery] string? template = null, CancellationToken ct = default) =>
        ExportAsync(roomId, template, "docx", ct);

    /// <summary>
    /// The same document as a PDF.
    ///
    /// A conversion of the .docx that <see cref="ExportDocx"/> would have handed over — not a
    /// second rendering of the minutes. One layout, so the file somebody prints and the file
    /// somebody edits cannot disagree, and <paramref name="template"/> means the same thing here.
    /// </summary>
    [HttpGet("export.pdf")]
    public Task<IActionResult> ExportPdf(
        Guid roomId, [FromQuery] string? template = null, CancellationToken ct = default) =>
        ExportAsync(roomId, template, "pdf", ct);

    private async Task<IActionResult> ExportAsync(
        Guid roomId, string? template, string format, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _minutesService.ExportAsync(
            roomId, userId.Value, User.GetEmail(), template, format, ct);

        return result.IsSuccess
            ? File(result.Value!.Bytes, result.Value!.ContentType, result.Value!.FileName)
            : Fail(result.Error, result.ErrorCode);
    }

    // ------------------------------------------------------------------ sharing

    /// <summary>
    /// The share dialog's state, creating the link on first ask.
    ///
    /// Host authority: who may read a record is the host's decision. The link is created
    /// INVITED_ONLY, so opening this dialog never publishes anything.
    /// </summary>
    [HttpGet("share")]
    public async Task<IActionResult> GetShare(Guid roomId, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        return RespondShare(await _minutesService.GetOrCreateShareAsync(roomId, userId.Value, ct));
    }

    /// <summary>Change the mode, downloads or expiry. Omitted fields are left as they are.</summary>
    [HttpPatch("share")]
    public async Task<IActionResult> UpdateShare(
        Guid roomId, [FromBody] UpdateMinutesShareRequest request, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        return RespondShare(await _minutesService.UpdateShareAsync(roomId, userId.Value, request, ct));
    }

    /// <summary>Kill the link. The URL already sent stops working and is not re-issued.</summary>
    [HttpDelete("share")]
    public async Task<IActionResult> RevokeShare(Guid roomId, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        return RespondShare(await _minutesService.RevokeShareAsync(roomId, userId.Value, ct));
    }

    [HttpPost("share/people")]
    public async Task<IActionResult> AddSharePerson(
        Guid roomId, [FromBody] AddMinutesSharePersonRequest request, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        return RespondShare(
            await _minutesService.AddSharePersonAsync(roomId, userId.Value, request.Email, ct));
    }

    /// <summary>Email in the query string, not the path: an address contains characters a route does not.</summary>
    [HttpDelete("share/people")]
    public async Task<IActionResult> RemoveSharePerson(
        Guid roomId, [FromQuery] string email, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        return RespondShare(await _minutesService.RemoveSharePersonAsync(roomId, userId.Value, email, ct));
    }

    private IActionResult RespondShare(Result<MinutesShareDto> result) =>
        result.IsSuccess ? Ok(result.Value) : Fail(result.Error, result.ErrorCode);

    /// <summary>One place the error codes become status codes, so two endpoints cannot disagree.</summary>
    private IActionResult Fail(string? error, string? code) => code switch
    {
        ErrorCodes.NotFound => NotFound(new ApiErrorResponse(error, code)),
        ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(error, code)),
        ErrorCodes.Unauthorized => StatusCode(401, new ApiErrorResponse(error, code)),
        ErrorCodes.InvalidState => BadRequest(new ApiErrorResponse(error, code)),
        ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(error, code)),
        ErrorCodes.ServiceUnavailable => StatusCode(503, new ApiErrorResponse(error, code)),
        _ => StatusCode(500, new ApiErrorResponse(error, code))
    };

    /// <summary>
    /// Ok, or the error code turned into a status. Generic because this controller answers with
    /// three different payloads and the mapping must not be able to differ between them — which
    /// is exactly what two hand-copied helpers were one edit away from.
    /// </summary>
    private IActionResult Respond<T>(Result<T> result) =>
        result.IsSuccess ? Ok(result.Value) : Fail(result.Error, result.ErrorCode);

    private IActionResult Respond(Result<MeetingMinutesDto> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Unauthorized => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Conflict => Conflict(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.InvalidState => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode))
        };
    }
}

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
/// Reading a biên bản from a share link.
///
/// WHY THIS IS [AllowAnonymous] AND THE REST OF THE SERVICE IS NOT
///     A public link has to work for somebody with no account — that is what the mode means. The
///     token IS the credential, so these three endpoints are the only unauthenticated surface in
///     this service and they are deliberately narrow: one document, read-only, and no way to ask
///     which rooms or workspaces exist.
///
///     A restricted link still resolves here, because a signed-in caller's identity arrives on the
///     same request when there is one: <c>User.GetUserId()</c> is null for an anonymous reader and
///     populated for a signed-in one, and the service decides from that. An anonymous hit on a
///     restricted link gets 401 — a sign-in prompt — rather than 404, because an invited person
///     clicking their own link has done nothing wrong.
///
/// WHAT A TOKEN NEVER BUYS
///     The minutes document, and nothing else. No participant list, no transcript, no recording,
///     no other minutes in the workspace. Everything reachable from here is one room's current
///     biên bản.
/// </summary>
[ApiController]
[Route("api/v1/shared/minutes")]
[AllowAnonymous]
public class SharedMinutesController : ControllerBase
{
    private readonly IMeetingMinutesService _minutesService;

    public SharedMinutesController(IMeetingMinutesService minutesService)
    {
        _minutesService = minutesService;
    }

    [HttpGet("{token}")]
    public async Task<IActionResult> Get(string token, CancellationToken ct = default)
    {
        var result = await _minutesService.GetSharedAsync(token, User.GetUserId(), User.GetEmail(), ct);

        return result.IsSuccess ? Ok(result.Value) : Fail(result.Error, result.ErrorCode);
    }

    /// <summary>
    /// <paramref name="template"/> works here exactly as it does inside the app: the recipient of
    /// a link is often the person who has to file the document, and which form they need — the
    /// Vietnamese one or the international one — is their business, not the sender's. The content
    /// is identical either way, so offering the choice gives nothing away.
    /// </summary>
    [HttpGet("{token}/export.docx")]
    public Task<IActionResult> ExportDocx(
        string token, [FromQuery] string? template = null, CancellationToken ct = default) =>
        ExportAsync(token, template, "docx", ct);

    [HttpGet("{token}/export.pdf")]
    public Task<IActionResult> ExportPdf(
        string token, [FromQuery] string? template = null, CancellationToken ct = default) =>
        ExportAsync(token, template, "pdf", ct);

    private async Task<IActionResult> ExportAsync(
        string token, string? template, string format, CancellationToken ct)
    {
        var result = await _minutesService.ExportSharedAsync(
            token, User.GetUserId(), User.GetEmail(), template, format, ct);

        return result.IsSuccess
            ? File(result.Value!.Bytes, result.Value!.ContentType, result.Value!.FileName)
            : Fail(result.Error, result.ErrorCode);
    }

    private IActionResult Fail(string? error, string? code) => code switch
    {
        // Revoked, expired and never-existed all arrive here as NotFound. Keeping them
        // indistinguishable is what stops the endpoint being an oracle for which links used to work.
        ErrorCodes.NotFound => NotFound(new ApiErrorResponse(error, code)),
        ErrorCodes.Unauthorized => StatusCode(401, new ApiErrorResponse(error, code)),
        ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(error, code)),
        ErrorCodes.InvalidState => BadRequest(new ApiErrorResponse(error, code)),
        ErrorCodes.ServiceUnavailable => StatusCode(503, new ApiErrorResponse(error, code)),
        _ => StatusCode(500, new ApiErrorResponse(error, code))
    };
}

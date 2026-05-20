using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Controllers;

[ApiController]
[Route("api/v1/translation-rooms/{roomId:guid}/audio-routes")]
[Authorize]
public class TranslationRoomAudioRouteController : ControllerBase
{
    private readonly ITranslationRoomAudioRouteService _audioRouteService;

    public TranslationRoomAudioRouteController(ITranslationRoomAudioRouteService audioRouteService)
    {
        _audioRouteService = audioRouteService;
    }

    [HttpPost("generate")]
    public async Task<IActionResult> GenerateRoutes(Guid roomId, CancellationToken ct)
    {
        var result = await _audioRouteService.GenerateRoutesAsync(roomId, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return BadRequest(new { Error = result.Error, Code = result.ErrorCode });
    }

    [HttpGet]
    public async Task<IActionResult> GetRoutes(Guid roomId, CancellationToken ct)
    {
        var result = await _audioRouteService.GetRoutesAsync(roomId, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return BadRequest(new { Error = result.Error, Code = result.ErrorCode });
    }

    [HttpPatch("{routeId:guid}/runtime")]
    public async Task<IActionResult> UpdateRuntimeContext(
        [FromRoute] Guid roomId, 
        [FromRoute] Guid routeId, 
        [FromBody] UpdateAudioRouteRuntimeContextDto dto, 
        CancellationToken ct)
    {
        var result = await _audioRouteService.UpdateRuntimeContextAsync(roomId, routeId, dto, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return BadRequest(new { Error = result.Error, Code = result.ErrorCode });
    }

    [HttpPatch("{routeId:guid}/voice-clone")]
    public async Task<IActionResult> ToggleVoiceClone(
        [FromRoute] Guid roomId, 
        [FromRoute] Guid routeId, 
        [FromBody] ToggleVoiceCloneDto dto, 
        CancellationToken ct)
    {
        var result = await _audioRouteService.ToggleVoiceCloneAsync(roomId, routeId, dto, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return BadRequest(new { Error = result.Error, Code = result.ErrorCode });
    }
}

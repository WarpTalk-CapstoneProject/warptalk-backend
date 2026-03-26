using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.TranscriptService.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class TranscriptsController : ControllerBase
{
    private readonly ITranscriptService _transcriptService;

    public TranscriptsController(ITranscriptService transcriptService)
    {
        _transcriptService = transcriptService;
    }

    [HttpPost]
    public async Task<IActionResult> StartTranscript([FromBody] CreateTranscriptRequest request, CancellationToken ct)
    {
        var result = await _transcriptService.StartTranscriptAsync(request, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetTranscript(Guid id, CancellationToken ct)
    {
        var result = await _transcriptService.GetTranscriptAsync(id, ct);
        if (!result.IsSuccess)
            return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value);
    }

    [HttpPost("{id}/audio")]
    public async Task<IActionResult> ProcessAudioChunk(Guid id, [FromBody] ProcessAudioChunkRequest request, CancellationToken ct)
    {
        var audioBytes = Convert.FromBase64String(request.Base64AudioData ?? string.Empty);
        var result = await _transcriptService.ProcessAudioChunkAsync(id, audioBytes, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Accepted();
    }

    [HttpPost("{id}/finalize")]
    public async Task<IActionResult> FinalizeTranscript(Guid id, CancellationToken ct)
    {
        var result = await _transcriptService.FinalizeTranscriptAsync(id, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return NoContent();
    }
}

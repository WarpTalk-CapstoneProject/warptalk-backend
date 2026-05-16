using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;

namespace WarpTalk.TranscriptService.API.Controllers;

[ApiController]
[Route("api/v1/transcripts/{transcriptId}/exports")]
[Authorize]
public class TranscriptExportsController : ControllerBase
{
    private readonly ITranscriptExportService _exportService;

    public TranscriptExportsController(ITranscriptExportService exportService)
    {
        _exportService = exportService;
    }

    [HttpPost]
    public async Task<IActionResult> CreateExport(Guid transcriptId, [FromBody] CreateTranscriptExportRequest request)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdString, out var userId))
            return Unauthorized();

        try
        {
            var result = await _exportService.CreateExportAsync(transcriptId, request, userId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
    }

    [HttpGet("{id}/download")]
    [AllowAnonymous] // Ideally should be authorized, but simple for MVP if using standard browser download without headers
    public async Task<IActionResult> DownloadExport(Guid transcriptId, Guid id)
    {
        // If security is required for downloads, uncomment below and remove [AllowAnonymous]
        // var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        // if (!Guid.TryParse(userIdString, out var userId))
        //     return Unauthorized();

        var userId = Guid.Empty; // Bypass for now if allowed anonymous

        try
        {
            var (fileBytes, contentType, fileName) = await _exportService.DownloadExportAsync(transcriptId, id, userId);
            return File(fileBytes, contentType, fileName);
        }
        catch (Exception ex)
        {
            return NotFound(new { Message = ex.Message });
        }
    }
}

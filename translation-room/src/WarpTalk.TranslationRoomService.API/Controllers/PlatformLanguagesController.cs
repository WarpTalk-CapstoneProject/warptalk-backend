using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Controllers;

[ApiController]
[Route("api/platform/languages")]
[Authorize(Roles = "PlatformAdmin")]
public class PlatformLanguagesController : ControllerBase
{
    private readonly ISupportedLanguageService _languageService;

    public PlatformLanguagesController(ISupportedLanguageService languageService)
    {
        _languageService = languageService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var languages = await _languageService.GetAllAsync(cancellationToken);
        return Ok(languages);
    }

    public record CreateLanguageRequest(string Code, string Name, string? NativeName);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateLanguageRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var language = await _languageService.CreateAsync(request.Code, request.Name, request.NativeName, cancellationToken);
            return Ok(language);
        }
        catch (System.InvalidOperationException ex)
        {
            return Conflict(new ApiErrorResponse(ex.Message, ErrorCodes.Conflict));
        }
        catch (System.ArgumentException ex)
        {
            return BadRequest(new ApiErrorResponse(ex.Message, ErrorCodes.ValidationError));
        }
    }

    public record UpdateLanguageRequest(string Name, string? NativeName);

    [HttpPut("{code}")]
    public async Task<IActionResult> Update(string code, [FromBody] UpdateLanguageRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var language = await _languageService.UpdateAsync(code, request.Name, request.NativeName, cancellationToken);
            return Ok(language);
        }
        catch (System.InvalidOperationException ex)
        {
            return NotFound(new ApiErrorResponse(ex.Message, ErrorCodes.NotFound));
        }
    }

    public record ToggleActiveRequest(bool IsActive);

    [HttpPatch("{code}/active")]
    public async Task<IActionResult> ToggleActive(string code, [FromBody] ToggleActiveRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var language = await _languageService.ToggleActiveAsync(code, request.IsActive, cancellationToken);
            return Ok(language);
        }
        catch (System.InvalidOperationException ex)
        {
            return NotFound(new ApiErrorResponse(ex.Message, ErrorCodes.NotFound));
        }
    }
}

using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Controllers;

[ApiController]
[Route("api/public/languages")]
[Authorize]
public class PublicLanguagesController : ControllerBase
{
    private readonly ISupportedLanguageService _languageService;

    public PublicLanguagesController(ISupportedLanguageService languageService)
    {
        _languageService = languageService;
    }

    [HttpGet]
    public async Task<IActionResult> GetActive(CancellationToken cancellationToken)
    {
        var languages = await _languageService.GetActiveAsync(cancellationToken);
        return Ok(languages);
    }
}

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Services;

namespace WarpTalk.TranslationRoomService.API.Controllers;

/// <summary>
/// WT-880: the languages the platform admin has published (enabled catalog rows), for any
/// signed-in user. Under <c>translation-rooms/</c> so the gateway's existing catch-all routes it;
/// the literal segment outranks <c>{id}</c>.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/translation-rooms/published-languages")]
public class PublishedLanguagesController : ControllerBase
{
    private readonly IPublishedLanguageService _languages;

    public PublishedLanguagesController(IPublishedLanguageService languages)
    {
        _languages = languages;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SupportedLanguageDto>>> Get(CancellationToken ct)
    {
        var result = await _languages.GetPublishedAsync(ct);
        return result.IsSuccess ? Ok(result.Value) : StatusCode(500, result.Error);
    }
}

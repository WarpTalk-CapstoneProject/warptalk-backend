using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared.Authorization;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Controllers;

/// <summary>
/// The language catalog room validation actually reads, for the System Admin portal.
///
/// Unlike the public listing this includes INACTIVE languages, which is the whole reason it
/// exists: "Korean is present and switched off" and "Korean is not in the catalog" produce the
/// same rejection and need completely different fixes.
///
/// Read-only. Deactivating a language stops every new room in it across the platform, and this
/// service has no message bus, so such a change could not be recorded in the admin audit log —
/// the same reason the auth user directory has no mutations either. A platform-wide switch that
/// leaves no trace of who threw it is not one to put behind a button.
/// </summary>
[ApiController]
[Route("api/v1/admin/languages")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public class AdminLanguagesController : ControllerBase
{
    private readonly ILanguageRepository _languageRepository;

    public AdminLanguagesController(ILanguageRepository languageRepository)
    {
        _languageRepository = languageRepository;
    }

    [HttpGet]
    public async Task<ActionResult<SupportedLanguageDto[]>> Get(CancellationToken ct)
    {
        var catalog = await _languageRepository.GetCatalogAsync(ct);
        return Ok(catalog
            .Select(l => LanguageMapper.ToDto(l.Code, l.Name, l.NativeName, l.IsActive))
            .ToArray());
    }
}

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.AuthService.API.Controllers;

/// <summary>
/// Where the platform's voice-clone consent stands, in aggregate.
///
/// COUNTS ONLY, and that is a boundary rather than a simplification. A cloned voice is biometric
/// data; a per-person list of who has agreed to being cloned is a register of biometric
/// permissions, and nothing an administrator does on this screen acts on a person. The individual
/// record already has a reader — the person it belongs to, through the ordinary voice consent
/// endpoints.
///
/// Read-only. Consent is append-only by design so that "what had they agreed to at the moment we
/// cloned them" stays answerable; an administrator who could write here could answer it wrongly.
/// </summary>
[ApiController]
[Route("api/v1/admin/voice-consent")]
[Authorize(Policy = SystemAdminAuthorization.PolicyName)]
public class AdminVoiceConsentController : ControllerBase
{
    private readonly IVoiceConsentRepository _voiceConsentRepository;

    public AdminVoiceConsentController(IVoiceConsentRepository voiceConsentRepository)
    {
        _voiceConsentRepository = voiceConsentRepository;
    }

    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        var snapshot = await _voiceConsentRepository.GetAdminSnapshotAsync(ct);

        return Ok(new
        {
            byStatus = snapshot.ByStatus.Select(s => new
            {
                consentType = s.ConsentType,
                status = s.Status,
                people = s.People,
            }),
            currentGrantsByTextVersion = snapshot.CurrentGrantsByTextVersion.Select(v => new
            {
                textVersion = v.TextVersion,
                people = v.People,
            }),
            totalDecisions = snapshot.TotalDecisions,
            // What new consent is being collected against, so the screen can mark every other
            // version as outdated without hardcoding the string on the client.
            currentTextVersion = VoiceConsentTextVersions.Current,
        });
    }
}

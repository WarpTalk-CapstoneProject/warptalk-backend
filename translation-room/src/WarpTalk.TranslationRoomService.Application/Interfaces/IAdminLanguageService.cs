using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.TranslationRoomService.Application.DTOs.Admin;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// Management of the language catalog room validation reads — <c>translation_room.supported_languages</c>
/// (WT-691). Add, rename, enable and soft-disable; never delete, because a room that already ran in
/// a language keeps naming it. Every change is recorded in the platform audit log before it is
/// saved, and abandoned if it cannot be recorded.
/// </summary>
public interface IAdminLanguageService
{
    Task<Result<IReadOnlyList<AdminLanguageDto>>> GetCatalogAsync(CancellationToken ct = default);

    Task<Result<AdminLanguageDto>> CreateAsync(
        AdminCreateLanguageRequest request, AdminActorContext actor, CancellationToken ct = default);

    Task<Result<AdminLanguageDto>> UpdateAsync(
        string code, AdminUpdateLanguageRequest request, AdminActorContext actor, CancellationToken ct = default);

    Task<Result<AdminLanguageDto>> EnableAsync(string code, AdminActorContext actor, CancellationToken ct = default);

    Task<Result<AdminLanguageDto>> DisableAsync(
        string code, AdminDisableLanguageRequest request, AdminActorContext actor, CancellationToken ct = default);
}

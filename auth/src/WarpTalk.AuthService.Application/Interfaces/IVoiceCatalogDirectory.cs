using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;

namespace WarpTalk.AuthService.Application.Interfaces;

/// <summary>
/// Read access to the per-language TTS voice catalog.
///
/// The catalog is owned by the AI side: tts_worker fetches Cartesia's public library and
/// caches it in Redis under "voice_catalog:{language}" (6h TTL). This service only reads
/// that cache — it never calls Cartesia, so the provider API key stays confined to the AI
/// workers. TranslationRoomHub.GetVoiceCatalog reads the very same key, which is what keeps
/// the Voice Profiles page and the in-meeting picker offering identical voices.
/// </summary>
public interface IVoiceCatalogDirectory
{
    /// <summary>
    /// Voices offered for <paramref name="language"/>, or an empty list when the cache has
    /// not been populated yet. Never throws: an empty catalog is a normal cold-start state
    /// (the cache fills on the AI worker's next synthesis for that language), not an error
    /// the caller should surface as a failure.
    /// </summary>
    Task<IReadOnlyList<VoiceCatalogItemDto>> GetAsync(string language, CancellationToken ct = default);
}

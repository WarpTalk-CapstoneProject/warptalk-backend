using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;

namespace WarpTalk.AuthService.Infrastructure.Clients;

/// <summary>
/// Used only where there is no Redis (local runs that fall back to an in-memory cache).
/// The catalog lives in Redis because the Python TTS worker puts it there, so without
/// Redis there is genuinely nothing to serve — an empty list is the honest answer, and it
/// is the same state callers already handle for a cold cache. Production refuses to start
/// without Redis, so this can never silently empty the catalog there.
/// </summary>
public class EmptyVoiceCatalogDirectory : IVoiceCatalogDirectory
{
    public Task<IReadOnlyList<VoiceCatalogItemDto>> GetAsync(string language, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<VoiceCatalogItemDto>>(Array.Empty<VoiceCatalogItemDto>());
}

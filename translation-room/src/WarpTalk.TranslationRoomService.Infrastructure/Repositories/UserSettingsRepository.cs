using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class UserSettingsRepository : IUserSettingsRepository
{
    private readonly TranslationRoomDbContext _context;

    public UserSettingsRepository(TranslationRoomDbContext context)
    {
        _context = context;
    }

    private class UserSettingsResult
    {
        public string DefaultSpeakLanguage { get; set; } = null!;
        public string DefaultListenLanguage { get; set; } = null!;
    }

    public async Task<(string DefaultSpeakLanguage, string DefaultListenLanguage)?> GetDefaultsAsync(Guid userId, CancellationToken ct = default)
    {
        // WT-65: Raw SQL to query cross-schema (auth.user_settings)
        var sql = @"
            SELECT default_speak_language as ""DefaultSpeakLanguage"", 
                   default_listen_language as ""DefaultListenLanguage""
            FROM auth.user_settings
            WHERE user_id = {0}
            LIMIT 1";

        var result = await _context.Database
            .SqlQueryRaw<UserSettingsResult>(sql, userId)
            .ToListAsync(ct);

        var settings = result.FirstOrDefault();
        if (settings == null) return null;

        return (settings.DefaultSpeakLanguage, settings.DefaultListenLanguage);
    }
}

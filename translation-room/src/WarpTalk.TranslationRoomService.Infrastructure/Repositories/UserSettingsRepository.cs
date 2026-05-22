using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class UserSettingsRepository : IUserSettingsRepository
{
    private readonly TranslationRoomDbContext _context;

    public UserSettingsRepository(TranslationRoomDbContext context)
    {
        _context = context;
    }



    public async Task<(string DefaultSpeakLanguage, string DefaultListenLanguage)?> GetDefaultsAsync(Guid userId, CancellationToken ct = default)
    {
        var settings = await _context.Set<UserSetting>()
            .FirstOrDefaultAsync(u => u.UserId == userId, ct);

        if (settings == null) return null;

        return (settings.DefaultSpeakLanguage ?? "vi-VN", settings.DefaultListenLanguage ?? "en-US");
    }
}

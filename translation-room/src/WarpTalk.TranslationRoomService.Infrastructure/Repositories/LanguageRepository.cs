using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class LanguageRepository : ILanguageRepository
{
    private readonly TranslationRoomDbContext _dbContext;

    public LanguageRepository(TranslationRoomDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> IsSupportedAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;

        var exists = await _dbContext.Database
            .SqlQueryRaw<string>("SELECT code FROM platform.supported_languages WHERE code = {0} AND is_active = true", code)
            .AnyAsync();

        return exists;
    }
}

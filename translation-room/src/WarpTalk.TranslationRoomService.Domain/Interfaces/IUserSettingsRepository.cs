using System;
using System.Threading.Tasks;

namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface IUserSettingsRepository
{
    Task<(string DefaultSpeakLanguage, string DefaultListenLanguage)?> GetDefaultsAsync(Guid userId, CancellationToken ct = default);
}

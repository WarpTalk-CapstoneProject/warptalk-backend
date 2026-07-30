namespace WarpTalk.TranslationRoomService.Application.Interfaces;

public sealed record UserLanguageDefaults(
    string DefaultSpeakLanguage,
    string DefaultListenLanguage);

public interface IUserSettingsDirectory
{
    Task<UserLanguageDefaults?> GetDefaultsAsync(
        Guid userId,
        CancellationToken ct = default);
}

using WarpTalk.Shared;
using WarpTalk.NotificationService.Application.DTOs;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Application.Services;

public class NotificationService : INotificationService
{
    private readonly IUnitOfWork _unitOfWork;

    public NotificationService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<NotificationPreferenceDto>> GetPreferencesAsync(Guid userId, CancellationToken ct = default)
    {
        var repo = _unitOfWork.Repository<NotificationPreference>();
        
        // We do a simple fallback if multiple matching items exist
        // Real implementation usually handles SingleOrDefault correctly
        var prefs = await repo.FindAsync(p => p.UserId == userId);
        var pref = prefs.FirstOrDefault();
        
        if (pref == null)
        {
            pref = new NotificationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                NotificationType = "SYSTEM",
                EmailEnabled = true,
                PushEnabled = true,
                InAppEnabled = true,
                UpdatedAt = DateTime.UtcNow
            };
            await repo.AddAsync(pref);
            await _unitOfWork.SaveChangesAsync();
        }

        return Result.Success(MapToDto(pref));
    }

    public async Task<Result<NotificationPreferenceDto>> UpdatePreferencesAsync(Guid userId, UpdateNotificationPreferenceRequest request, CancellationToken ct = default)
    {
        var repo = _unitOfWork.Repository<NotificationPreference>();
        var prefs = await repo.FindAsync(p => p.UserId == userId);
        var pref = prefs.FirstOrDefault();

        if (pref == null)
            return Result.Failure<NotificationPreferenceDto>("Preferences not found", ErrorCodes.NotFound);

        if (request.EmailEnabled.HasValue) pref.EmailEnabled = request.EmailEnabled.Value;
        if (request.PushEnabled.HasValue) pref.PushEnabled = request.PushEnabled.Value;
        if (request.InAppEnabled.HasValue) pref.InAppEnabled = request.InAppEnabled.Value;

        pref.UpdatedAt = DateTime.UtcNow;
        repo.Update(pref);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(MapToDto(pref));
    }

    public async Task<Result> SendNotificationAsync(Guid userId, string templateCode, Dictionary<string, string> variables, CancellationToken ct = default)
    {
        // Mock notification send logic
        await Task.Delay(100, ct);
        return Result.Success();
    }

    private NotificationPreferenceDto MapToDto(NotificationPreference p) =>
        new NotificationPreferenceDto(
            p.Id,
            p.UserId,
            p.NotificationType ?? "SYSTEM",
            p.EmailEnabled,
            p.PushEnabled,
            p.InAppEnabled,
            p.UpdatedAt
        );
}

using Grpc.Core;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.API.GrpcServices;

public class NotificationGrpcServiceImpl : NotificationGrpcService.NotificationGrpcServiceBase
{
    private readonly IUnitOfWork _unitOfWork;

    public NotificationGrpcServiceImpl(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public override async Task<SendNotificationResponse> SendNotification(SendNotificationRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            throw GrpcErrors.Required("User ID");

        if (!Guid.TryParse(request.UserId, out _))
            throw GrpcErrors.InvalidId("User");

        // Placeholder: real dispatch logic (Email/Push/In-App) to be wired later
        var notificationId = Guid.NewGuid().ToString();

        return new SendNotificationResponse
        {
            Success = true,
            NotificationId = notificationId
        };
    }

    public override async Task<GetUserPreferencesResponse> GetUserPreferences(GetUserPreferencesRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.UserId, out var parsedUserId))
            throw GrpcErrors.InvalidId("User");

        var repo = _unitOfWork.Repository<NotificationPreference>();
        var allPrefs = await repo.FindAsync(p => p.UserId == parsedUserId);
        var prefsList = allPrefs.ToList();

        var response = new GetUserPreferencesResponse
        {
            UserId = request.UserId
        };

        foreach (var pref in prefsList)
        {
            response.Preferences.Add(new NotificationChannelPreference
            {
                Channel = pref.NotificationType ?? "SYSTEM",
                Enabled = pref.EmailEnabled || pref.PushEnabled || pref.InAppEnabled
            });
        }

        return response;
    }
}

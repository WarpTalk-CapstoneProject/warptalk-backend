using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.Shared.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Workers;

public class TranslationRoomReminderWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TranslationRoomReminderWorker> _logger;
    private readonly ConcurrentDictionary<Guid, bool> _remindersSent = new();

    public TranslationRoomReminderWorker(IServiceProvider serviceProvider, ILogger<TranslationRoomReminderWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Translation Room Reminder Worker started.");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

                // Find rooms scheduled in the next 15 minutes
                var now = DateTime.UtcNow;
                var threshold = now.AddMinutes(15);
                
                var upcomingRooms = await unitOfWork.TranslationRoomRepository.FindAsync(
                    r => r.Status == "SCHEDULED" && 
                         r.ScheduledAt.HasValue && 
                         r.ScheduledAt.Value > now && 
                         r.ScheduledAt.Value <= threshold, 
                    ct: stoppingToken);

                foreach (var room in upcomingRooms)
                {
                    if (_remindersSent.ContainsKey(room.Id)) continue; // Already sent

                    // Try adding to dictionary to prevent duplicates
                    if (_remindersSent.TryAdd(room.Id, true))
                    {
                        // Fetch participants
                        var participants = await unitOfWork.TranslationRoomParticipantRepository.FindAsync(
                            p => p.TranslationRoomId == room.Id, 
                            ct: stoppingToken);

                        var meetingLink = $"http://localhost:3000/room/{room.TranslationRoomCode}"; 

                        // For UAT, we send it to participants. In a real system, we might have InvitedEmails saved.
                        foreach (var participant in participants)
                        {
                            await emailService.SendMeetingReminderAsync(
                                "participant@warptalk.local",
                                participant.DisplayName,
                                meetingLink,
                                room.Title ?? "Upcoming Meeting",
                                room.ScheduledAt!.Value.ToString("f"),
                                stoppingToken);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in Translation Room Reminder Worker.");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // Run every minute
        }
    }
}

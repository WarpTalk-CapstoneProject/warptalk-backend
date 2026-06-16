using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.Shared.Interfaces;

public interface IEmailService
{
    Task SendMeetingInvitationAsync(string toEmail, string participantName, string meetingLink, string meetingTitle, string scheduledTime, CancellationToken ct = default);
    Task SendMeetingReminderAsync(string toEmail, string participantName, string meetingLink, string meetingTitle, string startsIn, CancellationToken ct = default);
}

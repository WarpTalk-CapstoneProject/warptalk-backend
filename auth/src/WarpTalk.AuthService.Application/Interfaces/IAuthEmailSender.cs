using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Application.Interfaces;

public interface IAuthEmailSender
{
    Task SendVerificationEmailAsync(User user, string token, CancellationToken ct = default);
    Task SendPasswordResetEmailAsync(User user, string token, CancellationToken ct = default);

    /// <summary>G10: staff access granted to an account, or offered to an address with none yet.</summary>
    Task SendStaffInvitationEmailAsync(
        string toEmail,
        string inviterName,
        string roleName,
        CancellationToken ct = default);
}

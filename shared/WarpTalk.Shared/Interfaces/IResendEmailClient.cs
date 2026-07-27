using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.Shared.Interfaces;

public record SendEmailRequest(
    string From,
    string To,
    string Subject,
    string HtmlBody,
    string? TextBody = null
);

public record SendEmailResponse(
    bool IsSuccess,
    string? MessageId,
    string? ErrorMessage
);

public interface IResendEmailClient
{
    Task<SendEmailResponse> SendEmailAsync(SendEmailRequest request, CancellationToken ct = default);
}

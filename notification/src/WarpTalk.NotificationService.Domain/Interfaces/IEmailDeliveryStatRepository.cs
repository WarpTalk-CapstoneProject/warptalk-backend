using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface IEmailDeliveryStatRepository
{
    /// <summary>Adds one send to the day's counter in a single atomic upsert; runs immediately.</summary>
    Task IncrementAsync(string templateKey, string locale, DateOnly day, bool succeeded, CancellationToken ct = default);

    /// <summary>Untracked. Every counter row from <paramref name="since"/> on, for one email or all.</summary>
    Task<IReadOnlyList<EmailDeliveryStat>> ListSinceAsync(string? templateKey, DateOnly since, CancellationToken ct = default);
}

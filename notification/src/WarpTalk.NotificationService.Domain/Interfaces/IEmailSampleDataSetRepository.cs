using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface IEmailSampleDataSetRepository
{
    Task AddAsync(EmailSampleDataSet set, CancellationToken ct = default);

    void Remove(EmailSampleDataSet set);

    /// <summary>Tracked, for updating.</summary>
    Task<EmailSampleDataSet?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Untracked, oldest first.</summary>
    Task<IReadOnlyList<EmailSampleDataSet>> ListAsync(string templateKey, CancellationToken ct = default);
}

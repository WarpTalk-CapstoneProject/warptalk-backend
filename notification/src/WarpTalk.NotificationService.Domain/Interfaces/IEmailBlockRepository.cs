using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Interfaces;

public interface IEmailBlockRepository
{
    Task AddAsync(EmailBlock block, CancellationToken ct = default);

    void Remove(EmailBlock block);

    /// <summary>Tracked, for updating.</summary>
    Task<EmailBlock?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Untracked. Every block of a kind (or all kinds), archived included.</summary>
    Task<IReadOnlyList<EmailBlock>> ListAsync(string? kind, CancellationToken ct = default);

    Task<bool> KeyExistsAsync(string kind, string key, Guid? exceptId, CancellationToken ct = default);

    /// <summary>Tracked. The layouts currently flagged default (normally one).</summary>
    Task<IReadOnlyList<EmailBlock>> GetDefaultLayoutsAsync(CancellationToken ct = default);

    /// <summary>Untracked. Active, published partials by key — what a send expands.</summary>
    Task<IReadOnlyDictionary<string, string>> GetPublishedPartialsAsync(CancellationToken ct = default);
}

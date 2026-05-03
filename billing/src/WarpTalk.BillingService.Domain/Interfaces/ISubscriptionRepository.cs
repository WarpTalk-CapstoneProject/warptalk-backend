// =======================================================
// ISubscriptionRepository.cs
// =======================================================

using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

/// <summary>
/// Repository for workspace subscriptions.
/// Billing ownership is workspace-based.
/// </summary>
public interface ISubscriptionRepository
{
    Task<Subscription?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get active subscription of a workspace
    /// </summary>
    Task<Subscription?> GetActiveByWorkspaceIdAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all subscriptions of a workspace
    /// </summary>
    Task<IReadOnlyList<Subscription>> GetByWorkspaceIdAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get subscriptions owned by a user
    /// </summary>
    Task<IReadOnlyList<Subscription>> GetByOwnerUserIdAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        Subscription subscription,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        Subscription subscription,
        CancellationToken cancellationToken = default);
}
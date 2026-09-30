using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

/// <summary>
/// One signed-in session as its owner sees it: a refresh-token family, not a token row.
///
/// Rotation replaces the token row every time the access token is refreshed, so a row id is not a
/// stable name for "the browser on my laptop" — it changes every half hour. The family id is what
/// survives rotation, and it is also the unit logout and reuse detection already revoke.
/// <paramref name="SignedInAt"/> is the family's first row; <paramref name="LastActiveAt"/> is its
/// live leaf, i.e. the last time that session refreshed.
/// </summary>
public sealed record UserSessionRow(
    Guid FamilyId,
    string? DeviceInfo,
    string? IpAddress,
    DateTime SignedInAt,
    DateTime LastActiveAt,
    DateTime ExpiresAt);

public interface IRefreshTokenRepository : IGenericRepository<RefreshToken>
{
    Task<RefreshToken?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default);

    // Revokes every non-revoked token in a rotation family — used when a rotated-out
    // (already-revoked) refresh token is presented again, signalling possible theft.
    Task RevokeFamilyAsync(Guid familyId, CancellationToken ct = default);
    Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Live sessions (one per family with an unrevoked, unexpired leaf), most recently active first.</summary>
    Task<IReadOnlyList<UserSessionRow>> GetActiveSessionsForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Revokes every live family this user has except <paramref name="keepFamilyId"/>.</summary>
    Task RevokeAllForUserExceptFamilyAsync(Guid userId, Guid keepFamilyId, CancellationToken ct = default);

    /// <summary>
    /// Distinct accounts that were issued a refresh token in <c>[from, to)</c>.
    ///
    /// A token row is inserted on every sign-in (password or Google) AND on every rotation, and
    /// rows are revoked rather than deleted, so this is an event history — unlike
    /// <c>users.last_login_at</c>, which only remembers the latest sign-in and so undercounts
    /// every past window. See <c>AdminUserService.GetInsightsAsync</c> for what it misses.
    /// </summary>
    Task<int> CountDistinctUsersIssuedBetweenAsync(
        DateTime from,
        DateTime to,
        CancellationToken ct = default);
}

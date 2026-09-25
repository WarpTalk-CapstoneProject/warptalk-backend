using System;
using System.Collections.Generic;
using System.Linq;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.AuthService.Application.Services;

/// <summary>
/// The guard rails of staff management, as pure functions so each one is tested on its own
/// (StaffGuardsTests) rather than only through a service with a database behind it.
///
/// Every rule answers one question an attacker — or a tired administrator — would otherwise get
/// the wrong answer to:
/// <list type="bullet">
/// <item><see cref="NotSelf"/>: can I change my own access? Never; that is the self-escalation
/// path, and it is also how a Super Admin locks themselves out by mistake.</item>
/// <item><see cref="CanGrant"/>: can I hand out a role? Only one whose permissions I hold myself,
/// and Super Admin only if I am one.</item>
/// <item><see cref="CanManage"/>: can I act on this person? Not if they hold more than I do.</item>
/// <item><see cref="KeepsASuperAdmin"/>: will anyone be left who can manage staff?</item>
/// <item><see cref="CanEditRole"/>: can I change what a role allows? Not a built-in one, not the
/// one I hold, and never beyond what I hold.</item>
/// </list>
/// </summary>
public static class StaffGuards
{
    public const string SelfMessage =
        "You cannot change your own staff access. Ask another administrator.";
    public const string LastSuperAdminMessage =
        "This is the last active Super Admin. Make someone else a Super Admin first, or nobody will be able to manage staff.";
    public const string BuiltInRoleMessage =
        "Built-in roles are read-only. Duplicate it to make a custom role you can edit.";
    public const string OwnRoleMessage =
        "You cannot edit the role you hold. Ask another administrator.";
    public const string GrantSuperAdminMessage =
        "Only a Super Admin can make someone a Super Admin.";
    public const string ManageSuperAdminMessage =
        "Only a Super Admin can change another Super Admin's access.";

    public static Result NotSelf(Guid actorId, Guid targetUserId) =>
        actorId == targetUserId ? Result.Failure(SelfMessage, ErrorCodes.Forbidden) : Result.Success();

    /// <summary>May <paramref name="actor"/> give someone a role with these permissions?</summary>
    public static Result CanGrant(StaffAccess actor, bool roleIsSuperAdmin, IReadOnlyCollection<string> rolePermissions)
    {
        if (!actor.IsStaff) return Result.Failure("Only active staff can manage staff.", ErrorCodes.Forbidden);
        if (actor.IsSuperAdmin) return Result.Success();
        if (roleIsSuperAdmin) return Result.Failure(GrantSuperAdminMessage, ErrorCodes.Forbidden);
        return WithinActor(actor, rolePermissions, "grant");
    }

    /// <summary>May <paramref name="actor"/> suspend, reactivate, remove or re-role this person?</summary>
    public static Result CanManage(StaffAccess actor, bool targetIsSuperAdmin, IReadOnlyCollection<string> targetPermissions)
    {
        if (!actor.IsStaff) return Result.Failure("Only active staff can manage staff.", ErrorCodes.Forbidden);
        if (actor.IsSuperAdmin) return Result.Success();
        if (targetIsSuperAdmin) return Result.Failure(ManageSuperAdminMessage, ErrorCodes.Forbidden);
        var beyond = targetPermissions.Where(code => !actor.Has(code)).ToList();
        return beyond.Count == 0
            ? Result.Success()
            : Result.Failure(
                $"This person holds permissions you do not ({string.Join(", ", beyond)}). Ask a Super Admin.",
                ErrorCodes.Forbidden);
    }

    /// <param name="targetIsActiveSuperAdmin">The person being changed is an active Super Admin now.</param>
    /// <param name="staysActiveSuperAdmin">…and still will be after the change.</param>
    /// <param name="otherActiveSuperAdmins">Active Super Admins other than this person.</param>
    public static Result KeepsASuperAdmin(bool targetIsActiveSuperAdmin, bool staysActiveSuperAdmin, int otherActiveSuperAdmins) =>
        targetIsActiveSuperAdmin && !staysActiveSuperAdmin && otherActiveSuperAdmins <= 0
            ? Result.Failure(LastSuperAdminMessage, ErrorCodes.Conflict)
            : Result.Success();

    public static Result CanEditRole(
        StaffAccess actor,
        Guid? actorRoleId,
        Guid roleId,
        bool roleIsBuiltIn,
        IReadOnlyCollection<string> newPermissions)
    {
        if (!actor.IsStaff) return Result.Failure("Only active staff can manage roles.", ErrorCodes.Forbidden);
        if (roleIsBuiltIn) return Result.Failure(BuiltInRoleMessage, ErrorCodes.Forbidden);
        if (actorRoleId == roleId) return Result.Failure(OwnRoleMessage, ErrorCodes.Forbidden);
        return actor.IsSuperAdmin ? Result.Success() : WithinActor(actor, newPermissions, "grant");
    }

    /// <summary>Every code must exist in the catalog; duplicates are folded, order is the catalog's.</summary>
    public static Result<IReadOnlyList<string>> NormalizePermissions(IEnumerable<string>? codes)
    {
        var requested = (codes ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToHashSet(StringComparer.Ordinal);
        var unknown = requested.Where(c => !AdminPermissions.IsKnown(c)).OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            return Result.Failure<IReadOnlyList<string>>(
                $"Unknown permission(s): {string.Join(", ", unknown)}.", ErrorCodes.ValidationError);
        }

        IReadOnlyList<string> ordered = AdminPermissions.All.Where(requested.Contains).ToList();
        return Result.Success(ordered);
    }

    private static Result WithinActor(StaffAccess actor, IReadOnlyCollection<string> permissions, string verb)
    {
        var beyond = permissions.Where(code => !actor.Has(code)).ToList();
        return beyond.Count == 0
            ? Result.Success()
            : Result.Failure(
                $"You cannot {verb} permissions you do not hold yourself: {string.Join(", ", beyond)}.",
                ErrorCodes.Forbidden);
    }
}

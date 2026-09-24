using System;
using System.Collections.Generic;

namespace WarpTalk.AuthService.Domain.Entities;

public partial class Role
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    /// <summary>
    /// Built-in and read-only. For a staff role this means one of the seeded roles
    /// (BuiltInStaffRoles); the pre-G10 platform and workspace roles are all system roles too.
    /// </summary>
    public bool IsSystem { get; set; }

    /// <summary><c>legacy</c> (admin/user/moderator/Owner/Admin/Member) or <c>platform_staff</c>.</summary>
    public string Scope { get; set; } = "legacy";

    /// <summary>Stable key of a staff role (super_admin, support, a custom role's slug). Null for legacy roles.</summary>
    public string? Slug { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Internal auth user reference.
    /// </summary>
    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Internal auth user reference.
    /// </summary>
    public Guid? UpdatedBy { get; set; }

    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Internal auth user reference.
    /// </summary>
    public Guid? DeletedBy { get; set; }

    public virtual User? CreatedByNavigation { get; set; }

    public virtual User? DeletedByNavigation { get; set; }

    public virtual ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();

    public virtual User? UpdatedByNavigation { get; set; }

    public virtual ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

    public virtual ICollection<StaffMember> StaffMembers { get; set; } = new List<StaffMember>();

    public virtual ICollection<StaffInvitation> StaffInvitations { get; set; } = new List<StaffInvitation>();
}

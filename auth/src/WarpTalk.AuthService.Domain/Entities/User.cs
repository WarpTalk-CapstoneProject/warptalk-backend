using System;
using System.Collections.Generic;

namespace WarpTalk.AuthService.Domain.Entities;

public partial class User
{
    public Guid Id { get; set; }

    public string Email { get; set; } = null!;

    public string PasswordHash { get; set; } = null!;

    public string FullName { get; set; } = null!;

    public string? AvatarUrl { get; set; }

    public string? Phone { get; set; }

    public string? PreferredLanguage { get; set; }

    public string? Timezone { get; set; }

    public bool IsActive { get; set; }

    public bool IsLocked { get; set; }

    public int FailedLoginAttempts { get; set; }

    public DateTime? LockedUntil { get; set; }

    public bool EmailVerified { get; set; }

    public DateTime? EmailVerifiedAt { get; set; }

    public string? GoogleId { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public string? LastLoginIp { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public virtual ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();

    public virtual ICollection<UserRole> UserRoleAssignedByNavigations { get; set; } = new List<UserRole>();

    public virtual ICollection<UserRole> UserRoleUsers { get; set; } = new List<UserRole>();

    public virtual UserSetting? UserSetting { get; set; }

    public virtual ICollection<WorkspaceInvitation> WorkspaceInvitations { get; set; } = new List<WorkspaceInvitation>();

    public virtual ICollection<Workspace> Workspaces { get; set; } = new List<Workspace>();
}

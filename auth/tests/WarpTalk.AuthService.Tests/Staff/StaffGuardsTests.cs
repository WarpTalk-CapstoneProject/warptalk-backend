using System;
using System.Collections.Generic;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using Xunit;

namespace WarpTalk.AuthService.Tests.Staff;

/// <summary>The G10 guard rails as pure rules, one question each.</summary>
public sealed class StaffGuardsTests
{
    private static readonly StaffAccess SuperAdmin = DelegateStaffAccessSource.SuperAdmin();

    private static readonly StaffAccess Lead = DelegateStaffAccessSource.Staff(
        AdminPermissions.StaffRead, AdminPermissions.StaffManage, AdminPermissions.WorkspacesRead);

    [Fact]
    public void Nobody_ChangesTheirOwnStaffAccess()
    {
        var id = Guid.NewGuid();

        var result = StaffGuards.NotSelf(id, id);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.True(StaffGuards.NotSelf(id, Guid.NewGuid()).IsSuccess);
    }

    [Fact]
    public void OnlyASuperAdmin_GrantsSuperAdmin()
    {
        Assert.True(StaffGuards.CanGrant(SuperAdmin, roleIsSuperAdmin: true, AdminPermissions.All).IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, StaffGuards.CanGrant(Lead, roleIsSuperAdmin: true, []).ErrorCode);
    }

    [Fact]
    public void AStaffManager_GrantsOnlyWhatTheyHoldThemselves()
    {
        Assert.True(StaffGuards.CanGrant(Lead, false, [AdminPermissions.WorkspacesRead]).IsSuccess);

        var beyond = StaffGuards.CanGrant(Lead, false, [AdminPermissions.WorkspacesRead, AdminPermissions.BillingAdjustCredit]);

        Assert.False(beyond.IsSuccess);
        Assert.Contains(AdminPermissions.BillingAdjustCredit, beyond.Error);
    }

    [Fact]
    public void InactiveStaff_GrantNothing()
    {
        Assert.False(StaffGuards.CanGrant(StaffAccess.None, false, []).IsSuccess);
        Assert.False(StaffGuards.CanManage(StaffAccess.None, false, []).IsSuccess);
    }

    [Fact]
    public void AStaffManager_CannotActOnSomeoneWhoHoldsMore()
    {
        Assert.Equal(ErrorCodes.Forbidden, StaffGuards.CanManage(Lead, targetIsSuperAdmin: true, []).ErrorCode);
        Assert.False(StaffGuards.CanManage(Lead, false, [AdminPermissions.BillingRead]).IsSuccess);
        Assert.True(StaffGuards.CanManage(Lead, false, [AdminPermissions.WorkspacesRead]).IsSuccess);
        Assert.True(StaffGuards.CanManage(SuperAdmin, true, AdminPermissions.All).IsSuccess);
    }

    [Theory]
    // target active super, stays super?, others → allowed?
    [InlineData(true, false, 0, false)]  // the last one would go
    [InlineData(true, false, 1, true)]   // another remains
    [InlineData(true, true, 0, true)]    // still a Super Admin after the change
    [InlineData(false, false, 0, true)]  // was never an active Super Admin
    public void TheLastActiveSuperAdmin_CannotBeTakenAway(bool isSuper, bool stays, int others, bool allowed)
    {
        var result = StaffGuards.KeepsASuperAdmin(isSuper, stays, others);

        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed) Assert.Equal(ErrorCodes.Conflict, result.ErrorCode);
    }

    [Fact]
    public void BuiltInRoles_AreReadOnly_EvenToASuperAdmin()
    {
        var result = StaffGuards.CanEditRole(SuperAdmin, null, Guid.NewGuid(), roleIsBuiltIn: true, []);

        Assert.Equal(StaffGuards.BuiltInRoleMessage, result.Error);
    }

    [Fact]
    public void NobodyEditsTheRoleTheyHold()
    {
        var roleId = Guid.NewGuid();

        var result = StaffGuards.CanEditRole(Lead, roleId, roleId, false, [AdminPermissions.StaffRead]);

        Assert.Equal(StaffGuards.OwnRoleMessage, result.Error);
    }

    [Fact]
    public void ARoleEdit_CannotGoBeyondTheEditor()
    {
        Assert.False(StaffGuards.CanEditRole(Lead, null, Guid.NewGuid(), false, [AdminPermissions.AuditExport]).IsSuccess);
        Assert.True(StaffGuards.CanEditRole(SuperAdmin, null, Guid.NewGuid(), false, [AdminPermissions.AuditExport]).IsSuccess);
    }

    [Fact]
    public void PermissionLists_AreValidatedAgainstTheCatalog_AndPutInCatalogOrder()
    {
        var ok = StaffGuards.NormalizePermissions([AdminPermissions.StaffRead, " workspaces.read ", AdminPermissions.StaffRead]);
        var bad = StaffGuards.NormalizePermissions(["workspaces.read", "billing.everything"]);

        Assert.Equal(new List<string> { AdminPermissions.WorkspacesRead, AdminPermissions.StaffRead }, ok.Value);
        Assert.False(bad.IsSuccess);
        Assert.Contains("billing.everything", bad.Error);
    }

    [Theory]
    [InlineData("Tier 2 Support", "tier_2_support")]
    [InlineData("Kế toán / Finance", "ke_toan_finance")]
    [InlineData("!!!", "role")]
    public void CustomRoleSlugs_AreAsciiSnakeCase(string name, string expected)
    {
        Assert.Equal(expected, StaffAdminService.Slugify(name));
    }
}

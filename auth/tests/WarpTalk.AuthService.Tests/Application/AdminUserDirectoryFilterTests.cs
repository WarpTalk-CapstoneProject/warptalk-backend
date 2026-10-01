using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WarpTalk.AuthService.Application.DTOs.Admin;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;
using WarpTalk.AuthService.Infrastructure.Repositories;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.AuthService.Tests.Application;

/// <summary>
/// The user directory's created-at and last-login filters, in three layers that need no Docker:
/// what the service rejects, what the repository's WHERE clause keeps (run over plain rows), and
/// that Npgsql can translate it (ToQueryString). Infrastructure/AdminUserDirectoryTests runs the
/// same filters against real PostgreSQL.
/// </summary>
public class AdminUserDirectoryFilterTests
{
    private static readonly DateTime Anchor = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly AdminUserService _service;
    private AdminUserDirectoryFilter? _captured;

    public AdminUserDirectoryFilterTests()
    {
        _unitOfWork.UserRepository.Returns(_users);
        _users
            .GetDirectoryAsync(
                Arg.Do<AdminUserDirectoryFilter>(f => _captured = f),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyList<AdminUserDirectoryRow>)Array.Empty<AdminUserDirectoryRow>(), 0));

        _service = new AdminUserService(
            _unitOfWork,
            Substitute.For<IAdminAuditRecorder>(),
            Substitute.For<ILogger<AdminUserService>>());
    }

    // ── Service validation ───────────────────────────────────

    [Fact]
    public async Task A_created_from_later_than_created_to_is_a_validation_error()
    {
        var result = await _service.GetDirectoryAsync(new AdminUserDirectoryQuery
        {
            CreatedFrom = Anchor.AddDays(2),
            CreatedTo = Anchor,
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Null(_captured);
    }

    [Fact]
    public async Task A_last_login_from_later_than_last_login_to_is_a_validation_error()
    {
        var result = await _service.GetDirectoryAsync(new AdminUserDirectoryQuery
        {
            LastLoginFrom = Anchor.AddDays(2),
            LastLoginTo = Anchor,
        });

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Null(_captured);
    }

    [Fact]
    public async Task Never_signed_in_cannot_be_combined_with_a_last_login_bound()
    {
        var result = await _service.GetDirectoryAsync(new AdminUserDirectoryQuery
        {
            NeverSignedIn = true,
            LastLoginFrom = Anchor,
        });

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Null(_captured);
    }

    [Fact]
    public async Task Never_signed_in_false_may_be_combined_with_a_last_login_bound()
    {
        var result = await _service.GetDirectoryAsync(new AdminUserDirectoryQuery
        {
            NeverSignedIn = false,
            LastLoginFrom = Anchor,
        });

        Assert.True(result.IsSuccess);
        Assert.False(_captured!.NeverSignedIn);
    }

    [Fact]
    public async Task Bounds_reach_the_repository_as_utc()
    {
        var unspecified = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Unspecified);

        var result = await _service.GetDirectoryAsync(new AdminUserDirectoryQuery
        {
            CreatedFrom = unspecified,
            CreatedTo = Anchor.AddDays(10),
            LastLoginFrom = unspecified,
            LastLoginTo = Anchor.AddDays(5),
            NeverSignedIn = false,
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(Anchor, _captured!.CreatedFrom);
        Assert.Equal(DateTimeKind.Utc, _captured.CreatedFrom!.Value.Kind);
        Assert.Equal(Anchor.AddDays(10), _captured.CreatedTo);
        Assert.Equal(Anchor, _captured.LastLoginFrom);
        Assert.Equal(Anchor.AddDays(5), _captured.LastLoginTo);
    }

    [Fact]
    public async Task Defaults_are_unchanged_when_no_new_parameter_is_sent()
    {
        var result = await _service.GetDirectoryAsync(new AdminUserDirectoryQuery());

        Assert.True(result.IsSuccess);
        Assert.Equal(new AdminUserDirectoryFilter(Status: "all"), _captured);
    }

    // ── Repository WHERE clause over plain rows ──────────────

    private static readonly User Early = NewUser(Anchor.AddDays(1), lastLoginAt: Anchor.AddDays(3));
    private static readonly User Middle = NewUser(Anchor.AddDays(5), lastLoginAt: Anchor.AddDays(9));
    private static readonly User Late = NewUser(Anchor.AddDays(9), lastLoginAt: null);

    private static User NewUser(DateTime createdAt, DateTime? lastLoginAt) => new()
    {
        Id = Guid.NewGuid(),
        Email = $"{Guid.NewGuid():N}@acme.com",
        FullName = "Someone",
        IsActive = true,
        EmailVerified = true,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        LastLoginAt = lastLoginAt,
    };

    private static List<Guid> Apply(AdminUserDirectoryFilter filter) =>
        UserRepository.ApplyFilters(new[] { Early, Middle, Late }.AsQueryable(), filter, Anchor.AddDays(30))
            .Select(u => u.Id)
            .ToList();

    [Fact]
    public void Created_window_is_inclusive_from_and_exclusive_to()
    {
        var ids = Apply(new AdminUserDirectoryFilter(CreatedFrom: Anchor.AddDays(1), CreatedTo: Anchor.AddDays(9)));

        Assert.Equal(new[] { Early.Id, Middle.Id }, ids);
    }

    [Fact]
    public void Last_login_window_excludes_accounts_that_never_signed_in()
    {
        var ids = Apply(new AdminUserDirectoryFilter(LastLoginFrom: Anchor.AddDays(3)));

        Assert.Equal(new[] { Early.Id, Middle.Id }, ids);

        var upper = Apply(new AdminUserDirectoryFilter(LastLoginTo: Anchor.AddDays(9)));
        Assert.Equal(new[] { Early.Id }, upper);
    }

    [Fact]
    public void Never_signed_in_splits_on_last_login_being_null()
    {
        Assert.Equal(new[] { Late.Id }, Apply(new AdminUserDirectoryFilter(NeverSignedIn: true)));
        Assert.Equal(new[] { Early.Id, Middle.Id }, Apply(new AdminUserDirectoryFilter(NeverSignedIn: false)));
        Assert.Equal(3, Apply(new AdminUserDirectoryFilter()).Count);
    }

    // ── Translation ──────────────────────────────────────────

    [Fact]
    public void The_new_filters_translate_to_sql()
    {
        using var context = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options);

        var filter = new AdminUserDirectoryFilter(
            Search: "ada",
            Status: "all",
            CreatedFrom: Anchor,
            CreatedTo: Anchor.AddDays(30),
            LastLoginFrom: Anchor,
            LastLoginTo: Anchor.AddDays(30),
            NeverSignedIn: false);

        var sql = UserRepository
            .ApplySort(UserRepository.ApplyFilters(context.Users, filter, Anchor), "last_login_desc")
            .ToQueryString();

        Assert.Contains("created_at >= @", sql);
        Assert.Contains("created_at < @", sql);
        Assert.Contains("last_login_at >= @", sql);
        Assert.Contains("last_login_at < @", sql);
        Assert.Contains("last_login_at IS NOT NULL", sql);
        Assert.Contains("ILIKE", sql);
    }
}

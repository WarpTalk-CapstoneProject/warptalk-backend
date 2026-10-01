using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Infrastructure.Persistence;
using WarpTalk.AuthService.Infrastructure.Repositories;
using Xunit;

namespace WarpTalk.AuthService.Tests.Infrastructure;

/// <summary>
/// WT-888 — real PostgreSQL. "Single use" is a promise only the database can keep: two uploads
/// racing with one challenge id must not both be checked, so the claim is one conditional UPDATE
/// and exactly one caller may see it succeed.
/// </summary>
public sealed class VoiceEnrollmentChallengeRepositoryTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private readonly Guid _userId = Guid.NewGuid();
    private DbContextOptions<AuthDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        _options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(_database.GetConnectionString())
            .Options;

        await using var context = new AuthDbContext(_options);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION uuidv7() RETURNS uuid AS $$
                SELECT gen_random_uuid();
            $$ LANGUAGE sql;
            """);
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private async Task<Guid> SeedAsync(DateTime expiresAt, Guid? userId = null)
    {
        await using var context = new AuthDbContext(_options);
        var challenge = new VoiceEnrollmentChallenge
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? _userId,
            Language = "en",
            Phrase = "apple river window garden yellow orange mountain pencil",
            ExpiresAt = expiresAt,
            CreatedAt = Now,
        };
        context.Set<VoiceEnrollmentChallenge>().Add(challenge);
        await context.SaveChangesAsync();
        return challenge.Id;
    }

    [Fact]
    public async Task OnlyOneOfTwoRacingClaimsSucceeds()
    {
        var id = await SeedAsync(Now.AddMinutes(10));

        await using var first = new AuthDbContext(_options);
        await using var second = new AuthDbContext(_options);
        var claims = await Task.WhenAll(
            new VoiceEnrollmentChallengeRepository(first).TryConsumeAsync(id, _userId, Now),
            new VoiceEnrollmentChallengeRepository(second).TryConsumeAsync(id, _userId, Now));

        Assert.Equal(1, claims.Count(claimed => claimed));

        await using var check = new AuthDbContext(_options);
        var row = await check.Set<VoiceEnrollmentChallenge>().SingleAsync(c => c.Id == id);
        Assert.Equal(Now, row.ConsumedAt);
    }

    [Fact]
    public async Task AnExpiredChallengeCannotBeClaimed()
    {
        var id = await SeedAsync(Now.AddSeconds(-1));

        await using var context = new AuthDbContext(_options);
        Assert.False(await new VoiceEnrollmentChallengeRepository(context).TryConsumeAsync(id, _userId, Now));
    }

    [Fact]
    public async Task SomebodyElsesChallengeCannotBeClaimedOrRead()
    {
        var id = await SeedAsync(Now.AddMinutes(10), userId: Guid.NewGuid());

        await using var context = new AuthDbContext(_options);
        var repository = new VoiceEnrollmentChallengeRepository(context);
        Assert.False(await repository.TryConsumeAsync(id, _userId, Now));
        Assert.Null(await repository.GetForUserAsync(id, _userId));
    }

    [Fact]
    public async Task IssuedCountIsPerAccountAndWindowed()
    {
        await SeedAsync(Now.AddMinutes(10));
        await SeedAsync(Now.AddMinutes(10));
        await SeedAsync(Now.AddMinutes(10), userId: Guid.NewGuid());

        await using var context = new AuthDbContext(_options);
        var repository = new VoiceEnrollmentChallengeRepository(context);
        Assert.Equal(2, await repository.CountIssuedSinceAsync(_userId, Now.AddMinutes(-10)));
        Assert.Equal(0, await repository.CountIssuedSinceAsync(_userId, Now.AddMinutes(1)));
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using WarpTalk.TranslationRoomService.Infrastructure.Repositories;

namespace WarpTalk.TranslationRoomService.Tests.Infrastructure;

/// <summary>
/// k8s multi-replica dedupe: the conditional status transition that decides which of several
/// concurrent Open / Start / End calls runs the once-per-transition side effects.
///
/// Real Postgres on purpose: the whole point is the single conditional UPDATE, which a mock can
/// only assume. Two DbContexts stand in for two replicas, each with its own connection.
/// </summary>
public class RoomStatusCompareAndSetTests : IAsyncLifetime
{
    private static readonly string[] Endable = ["IN_PROGRESS", "PAUSED", "WAITING", "OPEN"];

    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private DbContextOptions<TranslationRoomDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        _options = new DbContextOptionsBuilder<TranslationRoomDbContext>()
            .UseNpgsql(_dbContainer.GetConnectionString())
            .Options;

        await using var db = new TranslationRoomDbContext(_options);
        await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS pgcrypto;");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE OR REPLACE FUNCTION public.uuidv7() RETURNS uuid AS $$ BEGIN RETURN gen_random_uuid(); END; $$ LANGUAGE plpgsql;");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE OR REPLACE FUNCTION public.uuid_generate_v7() RETURNS uuid AS $$ BEGIN RETURN gen_random_uuid(); END; $$ LANGUAGE plpgsql;");
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _dbContainer.DisposeAsync();

    private async Task<Guid> SeedRoomAsync(string status)
    {
        var now = DateTime.UtcNow;
        var room = new TranslationRoom
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = Guid.NewGuid(),
            HostId = Guid.NewGuid(),
            Title = "Ends once",
            TranslationRoomCode = Guid.NewGuid().ToString("N")[..12],
            Status = status,
            TranslationRoomType = "INSTANT",
            MaxParticipants = 100,
            SourceLanguage = "vi",
            TargetLanguages = "[\"en\"]",
            Settings = "{}",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        await using var db = new TranslationRoomDbContext(_options);
        db.Set<TranslationRoom>().Add(room);
        await db.SaveChangesAsync();
        return room.Id;
    }

    private async Task<bool> EndFromAnotherReplicaAsync(Guid roomId)
    {
        await using var db = new TranslationRoomDbContext(_options);
        return await new TranslationRoomRepository(db).TryTransitionStatusAsync(roomId, Endable, "ENDED");
    }

    [Fact]
    public async Task ConcurrentEnds_ExactlyOneWins_AndTheRoomIsEnded()
    {
        var roomId = await SeedRoomAsync("IN_PROGRESS");

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => EndFromAnotherReplicaAsync(roomId)));

        outcomes.Count(won => won).Should().Be(1);
        await using var db = new TranslationRoomDbContext(_options);
        var room = await db.Set<TranslationRoom>().AsNoTracking().SingleAsync(r => r.Id == roomId);
        room.Status.Should().Be("ENDED");
    }

    [Fact]
    public async Task AnAlreadyEndedRoom_IsNotEndedAgain()
    {
        var roomId = await SeedRoomAsync("ENDED");

        (await EndFromAnotherReplicaAsync(roomId)).Should().BeFalse();
    }

    [Fact]
    public async Task AnOpenBooking_CanBeEnded()
    {
        var roomId = await SeedRoomAsync("OPEN");

        (await EndFromAnotherReplicaAsync(roomId)).Should().BeTrue();
    }

    [Fact]
    public async Task ConcurrentOpens_ExactlyOneWins()
    {
        var roomId = await SeedRoomAsync("SCHEDULED");

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var db = new TranslationRoomDbContext(_options);
            return await new TranslationRoomRepository(db).TryTransitionStatusAsync(roomId, ["SCHEDULED"], "OPEN");
        }));

        outcomes.Count(won => won).Should().Be(1);
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Infrastructure.Persistence;
using WarpTalk.AuthService.Infrastructure.Repositories;
using Xunit;

namespace WarpTalk.AuthService.Tests.Infrastructure;

/// <summary>
/// Real PostgreSQL. The snapshot rests on a correlated NOT EXISTS that picks the newest row per
/// (user, consent type) — the obvious LINQ spelling for that does not translate on this provider,
/// and a version of this query that quietly counted every GRANTED row instead would report
/// everyone who has ever agreed as currently consenting, including those who withdrew.
/// </summary>
public sealed class AdminVoiceConsentSnapshotTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private static readonly DateTime Anchor = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private const string OldVersion = "2026-01-04.v1";
    private const string NewVersion = VoiceConsentTextVersions.Current;

    private readonly Guid _steady = Guid.NewGuid();
    private readonly Guid _withdrawn = Guid.NewGuid();
    private readonly Guid _returned = Guid.NewGuid();
    private readonly Guid _onOldWording = Guid.NewGuid();
    private readonly Guid _sameInstant = Guid.NewGuid();

    private AuthDbContext _context = null!;
    private VoiceConsentRepository _repository = null!;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();

        _context = new AuthDbContext(
            new DbContextOptionsBuilder<AuthDbContext>()
                .UseNpgsql(_database.GetConnectionString())
                .Options);

        await _context.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION uuidv7() RETURNS uuid AS $$
            DECLARE
                timestamp_ms bigint;
                timestamp_hex text;
                uuid_hex text;
            BEGIN
                timestamp_ms := (extract(epoch from clock_timestamp()) * 1000)::bigint;
                timestamp_hex := lpad(to_hex(timestamp_ms), 12, '0');
                uuid_hex := timestamp_hex || '7' || lpad(to_hex((random() * 4095)::integer), 3, '0')
                    || '8' || lpad(to_hex((random() * 4095)::integer), 3, '0')
                    || lpad(to_hex((random() * 281474976710655)::bigint), 12, '0');
                RETURN uuid_hex::uuid;
            END;
            $$ LANGUAGE plpgsql;
            """);
        await _context.Database.EnsureCreatedAsync();

        _repository = new VoiceConsentRepository(_context);
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _database.DisposeAsync();
    }

    private async Task SeedAsync()
    {
        _context.Set<VoiceConsent>().AddRange(
            // Granted once and never changed.
            Consent(_steady, VoiceConsentStatuses.Granted, NewVersion, Anchor),

            // Granted, then withdrawn. Counting GRANTED rows would still count this person.
            Consent(_withdrawn, VoiceConsentStatuses.Granted, NewVersion, Anchor),
            Consent(_withdrawn, VoiceConsentStatuses.Revoked, NewVersion, Anchor.AddDays(2)),

            // Withdrew, then agreed again. The newest row is the answer in both directions.
            Consent(_returned, VoiceConsentStatuses.Granted, OldVersion, Anchor),
            Consent(_returned, VoiceConsentStatuses.Revoked, OldVersion, Anchor.AddDays(1)),
            Consent(_returned, VoiceConsentStatuses.Granted, NewVersion, Anchor.AddDays(3)),

            // Live grant, given under wording that has since been replaced.
            Consent(_onOldWording, VoiceConsentStatuses.Granted, OldVersion, Anchor.AddDays(1)));

        await _context.SaveChangesAsync();

        // Two decisions in the same millisecond. created_at cannot separate them; the uuidv7 id
        // can, and the later insert must win.
        var earlier = Consent(_sameInstant, VoiceConsentStatuses.Granted, NewVersion, Anchor.AddDays(4));
        var later = Consent(_sameInstant, VoiceConsentStatuses.Revoked, NewVersion, Anchor.AddDays(4));
        earlier.Id = Guid.Parse("01920000-0000-7000-8000-000000000001");
        later.Id = Guid.Parse("01920000-0000-7000-8000-000000000002");
        _context.Set<VoiceConsent>().AddRange(earlier, later);

        await _context.SaveChangesAsync();
    }

    private static VoiceConsent Consent(
        Guid userId,
        string status,
        string textVersion,
        DateTime at) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ConsentType = VoiceConsentTypes.VoiceClone,
        ConsentStatus = status,
        ConsentTextVersion = textVersion,
        GrantedAt = status == VoiceConsentStatuses.Granted ? at : null,
        RevokedAt = status == VoiceConsentStatuses.Revoked ? at : null,
        CreatedAt = at,
    };

    [Fact]
    public async Task The_snapshot_translates_to_SQL_at_all()
    {
        var snapshot = await _repository.GetAdminSnapshotAsync();

        Assert.NotNull(snapshot);
    }

    [Fact]
    public async Task Only_the_newest_decision_per_person_counts()
    {
        // Five people, nine rows. Counting rows by status would say four are granted and three
        // revoked; the truth is three granted and two revoked.
        var snapshot = await _repository.GetAdminSnapshotAsync();

        Assert.Equal(9, snapshot.TotalDecisions);

        var granted = snapshot.ByStatus.Single(s => s.Status == VoiceConsentStatuses.Granted);
        var revoked = snapshot.ByStatus.Single(s => s.Status == VoiceConsentStatuses.Revoked);

        Assert.Equal(3, granted.People); // steady, returned, onOldWording
        Assert.Equal(2, revoked.People); // withdrawn, sameInstant
        Assert.Equal(5, snapshot.ByStatus.Sum(s => s.People));
    }

    [Fact]
    public async Task A_person_who_agreed_again_after_withdrawing_counts_as_granted()
    {
        // The mirror of the case above. A query that took the FIRST row, or the GRANTED row,
        // would get one of these two people wrong and look right on the other.
        var snapshot = await _repository.GetAdminSnapshotAsync();

        Assert.Equal(
            3,
            snapshot.ByStatus.Single(s => s.Status == VoiceConsentStatuses.Granted).People);
    }

    [Fact]
    public async Task Two_decisions_in_the_same_millisecond_resolve_by_id()
    {
        // created_at ties. The ids are uuidv7, so the larger one is the later insert — without
        // that tiebreak the plan picks whichever row it reaches first and the answer is a
        // coin toss between "granted" and "revoked" for that person.
        var snapshot = await _repository.GetAdminSnapshotAsync();

        Assert.Equal(
            2,
            snapshot.ByStatus.Single(s => s.Status == VoiceConsentStatuses.Revoked).People);
    }

    [Fact]
    public async Task Live_grants_are_broken_down_by_the_wording_they_were_given_under()
    {
        // The question the version column exists to answer: after the wording changed, how many
        // live grants are still against the old one.
        var snapshot = await _repository.GetAdminSnapshotAsync();

        Assert.Equal(2, snapshot.CurrentGrantsByTextVersion.Count);
        Assert.Equal(NewVersion, snapshot.CurrentGrantsByTextVersion[0].TextVersion);
        Assert.Equal(2, snapshot.CurrentGrantsByTextVersion[0].People); // steady, returned
        Assert.Equal(OldVersion, snapshot.CurrentGrantsByTextVersion[1].TextVersion);
        Assert.Equal(1, snapshot.CurrentGrantsByTextVersion[1].People); // onOldWording
    }

    [Fact]
    public async Task A_withdrawn_consent_does_not_appear_under_any_wording()
    {
        // `_returned` granted under the old wording first. If the version breakdown looked at all
        // rows instead of current ones, that superseded grant would still be counted there.
        var snapshot = await _repository.GetAdminSnapshotAsync();

        Assert.Equal(3, snapshot.CurrentGrantsByTextVersion.Sum(v => v.People));
    }

    [Fact]
    public void The_snapshot_carries_no_user_ids()
    {
        // The boundary, asserted on the type rather than on an instance: a per-person list of who
        // agreed to being cloned is a register of biometric permissions.
        var names = typeof(Domain.Interfaces.AdminVoiceConsentSnapshot)
            .GetProperties()
            .SelectMany(p => new[] { p.Name, p.PropertyType.Name })
            .ToArray();

        Assert.DoesNotContain(names, n => n.Contains("User", StringComparison.Ordinal));
    }
}

using System.Linq;
using Microsoft.EntityFrameworkCore;
using WarpTalk.AuthService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.AuthService.Tests.Infrastructure;

/// <summary>
/// No entity in this context may carry a SHADOW foreign key.
///
/// WHAT WENT WRONG WITHOUT THIS
///     VoiceConsent was configured as
///     <c>HasOne(d => d.VoiceProfile).WithMany().HasForeignKey(d => d.VoiceProfileId)</c>. The
///     parameterless <c>WithMany()</c> says the principal has no navigation back — but
///     <c>VoiceProfile.VoiceConsents</c> exists, so convention picked it up as a SECOND
///     relationship and, finding VoiceProfileId already used by the first, invented a shadow key
///     called <c>VoiceProfileId1</c>.
///
///     Shadow properties belong to the entity type, so it joined the SELECT list of every read of
///     voice_consents and Postgres answered <c>42703: column v.VoiceProfileId1 does not exist</c>.
///     UserServiceGrpc.HasVoiceCloneConsent therefore threw on every call for every user, and
///     both of its callers fail closed — so voice cloning could not be enabled by any path in the
///     product, while the only signal was a `warn` that read like a transient network blip.
///
/// WHY THE TEST IS SHAPED THIS WAY
///     A test asserting "VoiceConsent has no VoiceProfileId1" would pass forever after the fix and
///     catch nothing else. This context is mapped onto an existing database with an explicit
///     HasColumnName on every property, so a shadow FK anywhere in it is always a mapping mistake
///     and always breaks reads of that whole table. Asking the model the general question costs
///     the same and covers every entity, including ones not written yet.
///
///     It is also a MODEL test, not a database one: the fault is in the model EF builds, it needs
///     no connection to observe, and every mocked repository test in this suite sailed past it.
/// </summary>
public sealed class AuthModelHasNoShadowForeignKeysTests
{
    private static AuthDbContext BuildContext() =>
        new(
            new DbContextOptionsBuilder<AuthDbContext>()
                // Never opened — building the model is a pure operation. The provider still has to
                // be a real one, because provider conventions take part in building it.
                .UseNpgsql("Host=model-only;Database=model-only")
                .Options);

    [Fact]
    public void NoEntityCarriesAShadowForeignKey()
    {
        using var context = BuildContext();

        var offenders = context.Model
            .GetEntityTypes()
            .SelectMany(entity => entity.GetForeignKeys()
                .SelectMany(foreignKey => foreignKey.Properties)
                .Where(property => property.IsShadowProperty())
                .Select(property => $"{entity.ClrType.Name}.{property.Name}"))
            .Distinct()
            .OrderBy(name => name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Shadow foreign key(s) in AuthDbContext: "
                + string.Join(", ", offenders)
                + ". EF adds these when a relationship is declared without naming the navigation "
                + "that already exists on the other side, and the column does not exist in the "
                + "database — so every SELECT over the table fails with 42703.");
    }

    [Fact]
    public void VoiceConsentAndVoiceProfileShareOneRelationship()
    {
        using var context = BuildContext();

        var consent = context.Model.FindEntityType(typeof(Domain.Entities.VoiceConsent))!;
        var toProfile = consent.GetForeignKeys()
            .Where(foreignKey =>
                foreignKey.PrincipalEntityType.ClrType == typeof(Domain.Entities.VoiceProfile))
            .ToList();

        // Two would mean the navigation on each side was bound to a different relationship, which
        // is the exact shape that produced VoiceProfileId1.
        Assert.Single(toProfile);
        Assert.Equal(
            nameof(Domain.Entities.VoiceConsent.VoiceProfileId),
            Assert.Single(toProfile[0].Properties).Name);
    }
}

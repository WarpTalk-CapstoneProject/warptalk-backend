using System;
using System.Linq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Infrastructure;

/// <summary>
/// The guard for the failure this context keeps producing.
///
/// TranslationRoomDbContext hand-maps every column with HasColumnName and has no snake_case
/// naming convention. A property added to an entity without a matching mapping therefore does
/// not fail at startup, and does not fail when the migration adds the column either — EF simply
/// asks Postgres for the CLR name. Postgres quotes it, finds no "UpdatedAt" beside updated_at,
/// and answers 42703 on EVERY SELECT that touches the table.
///
/// That is what took History and Schedules down on 2026-08-20:
/// TranslationRoomArtifact.UpdatedAt shipped mapped in the entity, staged in the migration, and
/// absent from this context. Two comments in that very block predicted it in those words.
///
/// So the rule is checked rather than described. This needs no database: the model is built from
/// the mapping alone.
/// </summary>
public sealed class MappedColumnNamesTests
{
    [Fact]
    public void EveryColumnIsNamedExplicitlyInSnakeCase()
    {
        using var context = new TranslationRoomDbContext(
            new DbContextOptionsBuilder<TranslationRoomDbContext>()
                .UseNpgsql("Host=not-connected;Database=model-only")
                .Options);

        var unnamed = context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties()
                .Select(property => new
                {
                    Entity = entity.ClrType.Name,
                    Property = property.Name,
                    Column = property.GetColumnName(),
                }))
            // A column carrying an upper-case letter is the CLR property name falling through,
            // because every deliberate name in this context is snake_case.
            .Where(column => column.Column.Any(char.IsUpper))
            .Select(column => $"{column.Entity}.{column.Property} -> \"{column.Column}\"")
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();

        unnamed.Should().BeEmpty(
            "every property must be given its column with HasColumnName; a name that survives "
            + "with capitals in it is the CLR property leaking through, and Postgres answers "
            + "42703 to it on every SELECT over that table");
    }
}

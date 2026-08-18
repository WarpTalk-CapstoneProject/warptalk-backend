using System;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// Every mapped property must name its own column.
///
/// WHAT HAPPENED
///     WT-411 added <c>WorkspaceDocument.IngestionFailureReason</c>, its DTO, its mapper, and its
///     migration — but not the <c>.HasColumnName("ingestion_failure_reason")</c> line. This
///     DbContext configures no naming convention (every one of its ~300 properties spells its
///     column out by hand), so EF sent the PROPERTY name to Postgres and production answered:
///
///         Npgsql.PostgresException 42703: column w.IngestionFailureReason does not exist
///
///     Every SELECT over workspace_documents failed. The Documents page 500'd on every request
///     for every member of every workspace, and the catch-all in
///     WorkspaceDocumentService.ListDocumentsAsync reported it as "an unexpected error", so the
///     wrong column name never reached anybody who could read it.
///
/// WHY A CONVENTION TEST RATHER THAN ONE ASSERTION
///     Pinning that one column would catch that one bug. The defect is not the column, it is that
///     a hand-maintained mapping has no failure mode short of production: the build is happy, the
///     unit tests mock the repository, and nothing looks at the SQL until Postgres does. Any
///     property added without its line lands the same way, and this file is the thing that has to
///     notice.
///
///     Snake_case is the check because it is what every existing column and every migration uses,
///     and it is exactly what a defaulted PascalCase property name is not.
/// </summary>
public class WorkspaceDbContextColumnMappingTests
{
    // Lowercase, digits and underscores. A PascalCase property name defaulting through fails on
    // the very first character.
    private static readonly Regex SnakeCase = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    private static WorkspaceDbContext BuildContext()
    {
        // Npgsql rather than InMemory: the mapping under test is relational, and InMemory has no
        // column names to inspect. No connection is opened — the model is built from the
        // OnModelCreating configuration alone.
        var options = new DbContextOptionsBuilder<WorkspaceDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        return new WorkspaceDbContext(options);
    }

    [Fact]
    public void EveryMappedColumnIsSnakeCase()
    {
        using var context = BuildContext();

        var offenders = context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties()
                .Select(property => new
                {
                    Entity = entity.ClrType.Name,
                    Property = property.Name,
                    Column = property.GetColumnName(),
                }))
            .Where(mapping => !string.IsNullOrEmpty(mapping.Column) && !SnakeCase.IsMatch(mapping.Column))
            .Select(mapping => $"{mapping.Entity}.{mapping.Property} -> \"{mapping.Column}\"")
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These properties have no HasColumnName, so EF is asking Postgres for a column that "
            + "does not exist. Every SELECT over the table fails at runtime — see this file's "
            + "summary for the outage this reproduces:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The specific column the outage was about, pinned by name as well.
    ///
    /// The convention test above would catch it, but only as one line in a list. Somebody reading
    /// a failure here should be able to see the actual mapping that was missing.
    /// </summary>
    [Fact]
    public void IngestionFailureReasonMapsToItsMigratedColumn()
    {
        using var context = BuildContext();

        var column = context.Model
            .FindEntityType(typeof(WarpTalk.WorkspaceService.Domain.Entities.WorkspaceDocument))!
            .FindProperty(nameof(WarpTalk.WorkspaceService.Domain.Entities.WorkspaceDocument.IngestionFailureReason))!
            .GetColumnName();

        Assert.Equal("ingestion_failure_reason", column);
    }
}

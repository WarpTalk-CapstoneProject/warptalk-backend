using System;

namespace WarpTalk.BillingService.Domain.Entities;

public partial class SchemaMigration
{
    public Guid Id { get; set; }

    public string MigrationKey { get; set; } = null!;

    public string MigrationName { get; set; } = null!;

    public string? Checksum { get; set; }

    public string? ScriptPath { get; set; }

    public string? Status { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public int? ExecutionTimeMs { get; set; }

    public string? ErrorMessage { get; set; }

    public string? AppliedBy { get; set; }

    public DateTime? CreatedAt { get; set; }
}

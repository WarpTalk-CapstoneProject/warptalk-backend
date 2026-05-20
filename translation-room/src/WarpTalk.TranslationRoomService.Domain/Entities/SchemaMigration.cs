using System;
using System.Collections.Generic;

namespace WarpTalk.TranslationRoomService.Domain.Entities;

public partial class SchemaMigration
{
    public Guid Id { get; set; }

    public string MigrationKey { get; set; } = null!;

    public string MigrationName { get; set; } = null!;

    public string Checksum { get; set; } = null!;

    public string? ScriptPath { get; set; }

    public string Status { get; set; } = null!;

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public int? ExecutionTimeMs { get; set; }

    public string? ErrorMessage { get; set; }

    public string? AppliedBy { get; set; }

    public DateTime CreatedAt { get; set; }
}

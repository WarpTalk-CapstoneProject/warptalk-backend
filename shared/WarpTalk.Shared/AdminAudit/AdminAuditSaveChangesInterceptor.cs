using System.Collections;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.Shared.Events;

namespace WarpTalk.Shared.AdminAudit;

/// <summary>Thrown from a save the platform audit log refused, so the save does not happen.</summary>
public sealed class AdminAuditRefusedException : Exception
{
    public AdminAuditRefusedException(string action)
        : base($"The change was not made because the admin action '{action}' could not be recorded in the audit log.")
    {
    }
}

/// <summary>
/// Holds each save an armed request makes to an audited subject until that change is in the audit log.
/// </summary>
/// <remarks>
/// A singleton that finds the request's <see cref="AdminAuditScope"/> through the
/// <see cref="IHttpContextAccessor"/>, so it is harmless on a save no request armed — a background
/// worker, a consumer, a customer's own call — and needs nothing from a pooled context.
/// </remarks>
public sealed class AdminAuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AdminAuditSaveChangesInterceptor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await RecordAsync(eventData.Context, cancellationToken);
        return result;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        RecordAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    private Task RecordAsync(DbContext? context, CancellationToken ct)
    {
        var services = _httpContextAccessor.HttpContext?.RequestServices;
        var scope = services?.GetService<AdminAuditScope>();
        if (context is null || scope?.Current is null) return Task.CompletedTask;
        return RecordPendingAsync(context, scope.Current, services!.GetRequiredService<IAdminAuditSink>(), ct);
    }

    /// <summary>
    /// Records the subject rows this save changes. Throws <see cref="AdminAuditRefusedException"/>
    /// when the store does not confirm, which aborts the save.
    /// </summary>
    public static async Task RecordPendingAsync(
        DbContext context, AdminAuditInvocation invocation, IAdminAuditSink sink, CancellationToken ct)
    {
        var records = Describe(context, invocation);
        if (records.Count == 0) return;

        foreach (var record in records)
        {
            if (!await sink.RecordAsync(record, ct))
                throw new AdminAuditRefusedException(invocation.Action);
        }

        invocation.AddRecorded(records);
    }

    private const int MaxSubjects = 20;
    private const int MaxFields = 40;
    private const int MaxValueLength = 300;

    /// <summary>Bookkeeping columns every write touches; a diff of them says nothing.</summary>
    private static readonly HashSet<string> NoiseProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "UpdatedAt", "UpdatedBy", "CreatedAt", "CreatedBy", "ModifiedAt", "ModifiedBy", "RowVersion",
        "ConcurrencyStamp", "Xmin", "xmin",
    };

    /// <summary>What the subject was called, in the order a person would look for it.</summary>
    private static readonly string[] LabelProperties =
        ["Name", "DisplayName", "Title", "InvoiceNumber", "Term", "SourceTerm", "Code", "Key", "Slug", "Email", "CompanyName"];

    public static IReadOnlyList<AdminAuditRecord> Describe(DbContext context, AdminAuditInvocation invocation)
    {
        var changed = context.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(entry => invocation.SubjectTypes.Any(type => type.IsInstanceOfType(entry.Entity)))
            .ToList();

        if (invocation.Aggregate)
            return changed.Count == 0 ? [] : [Summarize(changed, invocation)];

        var entries = changed.Take(MaxSubjects).ToList();

        var records = new List<AdminAuditRecord>(entries.Count);
        foreach (var entry in entries)
        {
            var (before, after) = Diff(entry);
            if (entry.State == EntityState.Modified && before.Count == 0 && after.Count == 0) continue;

            var entityId = KeyOf(entry) ?? invocation.RouteEntityId;
            var entityKey = entityId is null ? NaturalKeyOf(entry) ?? invocation.RouteEntityKey : null;
            records.Add(new AdminAuditRecord
            {
                Action = invocation.Action,
                EntityType = invocation.EntityType,
                EntityId = entityId,
                EntityKey = entityKey,
                EntityLabel = LabelOf(entry),
                WorkspaceId = GuidProperty(entry, "WorkspaceId") ?? invocation.RouteWorkspaceId,
                ActorId = invocation.ActorId,
                Reason = invocation.Reason,
                Result = AdminAuditResults.Succeeded,
                // The store de-duplicates on (source, correlation, action, entity id). Rows with no
                // GUID — pricing keys, policy keys — would all collide on a null id, so each one's
                // key is folded into its correlation instead.
                CorrelationId = entityId is null && entityKey is not null
                    ? Bound(invocation.CorrelationId + "#" + entityKey, MaxCorrelationLength)
                    : invocation.CorrelationId,
                BeforeSummary = before.Count == 0 ? null : before,
                AfterSummary = after.Count == 0 ? null : after,
                Metadata = invocation.Metadata,
            });
        }

        return records;
    }

    /// <summary>One entry for a whole save: how many rows of each kind, and the first few names.</summary>
    private static AdminAuditRecord Summarize(IReadOnlyList<EntityEntry> changed, AdminAuditInvocation invocation)
    {
        foreach (var entry in changed.Where(entry => entry.State == EntityState.Added)) KeyOf(entry);

        string Count(EntityState state) =>
            changed.Count(entry => entry.State == state).ToString(CultureInfo.InvariantCulture);
        var labels = changed.Select(LabelOf).OfType<string>().Take(10).ToList();
        var after = new Dictionary<string, string?>
        {
            ["added"] = Count(EntityState.Added),
            ["modified"] = Count(EntityState.Modified),
            ["deleted"] = Count(EntityState.Deleted),
        };
        if (labels.Count > 0)
            after["examples"] = Format(string.Join(", ", labels));

        return new AdminAuditRecord
        {
            Action = invocation.Action,
            EntityType = invocation.EntityType,
            EntityId = invocation.RouteEntityId,
            EntityKey = invocation.RouteEntityKey,
            WorkspaceId = invocation.RouteWorkspaceId,
            ActorId = invocation.ActorId,
            Reason = invocation.Reason,
            Result = AdminAuditResults.Succeeded,
            // A bulk operation may save in batches; each is its own entry.
            CorrelationId = invocation.Recorded.Count == 0
                ? invocation.CorrelationId
                : Bound($"{invocation.CorrelationId}#{invocation.Recorded.Count + 1}", MaxCorrelationLength),
            AfterSummary = after,
            Metadata = invocation.Metadata,
        };
    }

    /// <summary>
    /// The subject's GUID key. A new row whose key the database would generate gets a v7 GUID now
    /// instead, so the entry can name the row it created — EF inserts the value it is given.
    /// </summary>
    private static Guid? KeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is not { Properties.Count: 1 } || key.Properties[0].ClrType != typeof(Guid)) return null;

        var property = entry.Property(key.Properties[0].Name);
        // A temporary value is EF's placeholder for one the database will return; naming it would
        // file the entry under an id no row ever has.
        if (property.CurrentValue is Guid id && id != Guid.Empty && !property.IsTemporary) return id;
        if (entry.State != EntityState.Added) return null;

        var generated = Guid.CreateVersion7();
        property.CurrentValue = generated;
        property.IsTemporary = false;
        return generated;
    }

    /// <summary>A single-column key that is not a GUID (a config row's key string), as text.</summary>
    private static string? NaturalKeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is not { Properties.Count: 1 } || key.Properties[0].ClrType == typeof(Guid)) return null;
        var value = entry.Property(key.Properties[0].Name).CurrentValue;
        return value is null ? null : AdminAuditRequestMetadata.Bound(Format(value), 100);
    }

    private const int MaxCorrelationLength = 100;

    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max];

    private static (Dictionary<string, string?> Before, Dictionary<string, string?> After) Diff(EntityEntry entry)
    {
        var before = new Dictionary<string, string?>();
        var after = new Dictionary<string, string?>();

        foreach (var property in entry.Properties)
        {
            if (before.Count >= MaxFields || after.Count >= MaxFields) break;
            var name = property.Metadata.Name;
            if (NoiseProperties.Contains(name) || property.Metadata.IsShadowProperty() || !IsScalar(property.Metadata.ClrType))
                continue;

            var key = SnakeCase(name);
            switch (entry.State)
            {
                case EntityState.Added:
                    if (property.CurrentValue is not null && !property.Metadata.IsPrimaryKey())
                        after[key] = Format(property.CurrentValue);
                    break;
                case EntityState.Deleted:
                    if (property.OriginalValue is not null && !property.Metadata.IsPrimaryKey())
                        before[key] = Format(property.OriginalValue);
                    break;
                case EntityState.Modified:
                    var original = Format(property.OriginalValue);
                    var current = Format(property.CurrentValue);
                    if (!string.Equals(original, current, StringComparison.Ordinal))
                    {
                        before[key] = original;
                        after[key] = current;
                    }
                    break;
            }
        }

        return (before, after);
    }

    private static string? LabelOf(EntityEntry entry)
    {
        foreach (var name in LabelProperties)
        {
            var property = entry.Metadata.FindProperty(name);
            if (property is null || property.ClrType != typeof(string)) continue;
            var value = entry.Property(name).CurrentValue as string ?? entry.Property(name).OriginalValue as string;
            if (!string.IsNullOrWhiteSpace(value)) return AdminAuditRequestMetadata.Bound(value, 200);
        }

        return null;
    }

    private static Guid? GuidProperty(EntityEntry entry, string name)
    {
        var property = entry.Metadata.FindProperty(name);
        if (property is null) return null;
        return entry.Property(name).CurrentValue switch
        {
            Guid id when id != Guid.Empty => id,
            _ => null,
        };
    }

    private static bool IsScalar(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(byte[])) return false;
        if (underlying == typeof(string) || underlying.IsPrimitive || underlying.IsEnum) return true;
        if (underlying == typeof(decimal) || underlying == typeof(Guid) || underlying == typeof(DateTime)
            || underlying == typeof(DateTimeOffset) || underlying == typeof(DateOnly) || underlying == typeof(TimeSpan))
            return true;
        return !typeof(IEnumerable).IsAssignableFrom(underlying) && underlying.IsValueType;
    }

    public static string? Format(object? value)
    {
        var text = value switch
        {
            null => null,
            string s => s,
            DateTime dt => (dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime())
                .ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
        if (text is null) return null;
        return text.Length <= MaxValueLength ? text : text[..MaxValueLength] + "…";
    }

    public static string SnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
                    builder.Append('_');
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}

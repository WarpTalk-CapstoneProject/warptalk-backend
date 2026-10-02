using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Domain.Entities;

namespace WarpTalk.TranscriptService.Application.GlossaryImportTemplates;

/// <inheritdoc cref="IGlossaryImportTemplateService"/>
public sealed class GlossaryImportTemplateService : IGlossaryImportTemplateService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IGlossaryImportTemplateStore _store;
    private readonly ILogger<GlossaryImportTemplateService> _logger;
    private readonly TimeProvider _clock;

    public GlossaryImportTemplateService(
        IGlossaryImportTemplateStore store,
        ILogger<GlossaryImportTemplateService> logger,
        TimeProvider? clock = null)
    {
        _store = store;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<Result<GlossaryImportTemplateDto>> GetAsync(CancellationToken ct = default)
    {
        var row = await _store.GetAsync(ct);
        return Result.Success(ToDto(row));
    }

    public async Task<Result<GlossaryImportTemplateDto>> UpdateAsync(
        UpdateGlossaryImportTemplateDto request, Guid actorId, CancellationToken ct = default)
    {
        var (config, error) = GlossaryImportTemplateRules.Validate(request);
        if (config is null)
            return Result.Failure<GlossaryImportTemplateDto>(error ?? "Invalid import template.", ErrorCodes.ValidationError);

        var saved = await _store.SaveAsync(
            JsonSerializer.Serialize(new StoredConfig(1, config.Columns, config.Samples), Json),
            actorId,
            _clock.GetUtcNow().UtcDateTime,
            ct);

        return Result.Success(ToDto(saved));
    }

    public async Task<Result<GlossaryImportTemplateDto>> ResetAsync(CancellationToken ct = default)
    {
        await _store.DeleteAsync(ct);
        return Result.Success(ToDto(null));
    }

    private GlossaryImportTemplateDto ToDto(GlossaryImportTemplate? row)
    {
        if (row is null)
            return new GlossaryImportTemplateDto(
                GlossaryImportTemplateDefaults.Columns,
                GlossaryImportTemplateDefaults.Samples,
                IsDefault: true,
                UpdatedAt: null,
                UpdatedBy: null);

        StoredConfig? stored = null;
        try
        {
            stored = JsonSerializer.Deserialize<StoredConfig>(row.Config, Json);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Stored glossary import template is not readable; serving the built-in default.");
        }

        if (stored?.Columns is null)
            return new GlossaryImportTemplateDto(
                GlossaryImportTemplateDefaults.Columns,
                GlossaryImportTemplateDefaults.Samples,
                IsDefault: true,
                UpdatedAt: row.UpdatedAt,
                UpdatedBy: row.UpdatedBy);

        return new GlossaryImportTemplateDto(
            CompleteColumns(stored.Columns),
            stored.Samples ?? new Dictionary<string, IReadOnlyDictionary<string, string>>(),
            IsDefault: false,
            UpdatedAt: row.UpdatedAt,
            UpdatedBy: row.UpdatedBy);
    }

    /// <summary>
    /// A column key added to the product after the admin last saved is served with its default
    /// settings at the end of its group, rather than missing from every file until someone
    /// re-saves the screen. Unknown keys (a key since removed) are dropped.
    /// </summary>
    private static IReadOnlyList<GlossaryImportTemplateColumnDto> CompleteColumns(IReadOnlyList<GlossaryImportTemplateColumnDto> stored)
    {
        var known = stored
            .Where(c => c is not null && GlossaryImportTemplateColumnKeys.All.Contains(c.Key))
            .GroupBy(c => c.Key)
            .Select(g => g.First() with { Aliases = g.First().Aliases ?? [] })
            .ToList();
        foreach (var fallback in GlossaryImportTemplateDefaults.Columns)
        {
            if (known.Any(c => c.Key == fallback.Key)) continue;
            known.Add(fallback with { Order = int.MaxValue });
        }
        return GlossaryImportTemplateRules.Order(known);
    }

    private sealed record StoredConfig(
        int Version,
        IReadOnlyList<GlossaryImportTemplateColumnDto>? Columns,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Samples);
}

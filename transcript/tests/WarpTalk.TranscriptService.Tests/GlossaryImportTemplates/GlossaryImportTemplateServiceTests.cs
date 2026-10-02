using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.GlossaryImportTemplates;
using WarpTalk.TranscriptService.Domain.Entities;
using Xunit;

namespace WarpTalk.TranscriptService.Tests.GlossaryImportTemplates;

/// <summary>
/// WT-880: the glossary import template is one admin-configured file shape. These pin the
/// defaults the PO chose, what an admin may and may not save, and that a saved configuration
/// survives a later product change (a new column key) instead of breaking every download.
/// </summary>
public sealed class GlossaryImportTemplateServiceTests
{
    private static readonly Guid Admin = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed class FakeStore : IGlossaryImportTemplateStore
    {
        public GlossaryImportTemplate? Row { get; set; }

        public Task<GlossaryImportTemplate?> GetAsync(CancellationToken ct = default) => Task.FromResult(Row);

        public Task<GlossaryImportTemplate> SaveAsync(string configJson, Guid actorId, DateTime nowUtc, CancellationToken ct = default)
        {
            Row = new GlossaryImportTemplate { Config = configJson, UpdatedAt = nowUtc, UpdatedBy = actorId };
            return Task.FromResult(Row);
        }

        public Task DeleteAsync(CancellationToken ct = default)
        {
            Row = null;
            return Task.CompletedTask;
        }
    }

    private static (GlossaryImportTemplateService Service, FakeStore Store) Create()
    {
        var store = new FakeStore();
        return (new GlossaryImportTemplateService(store, NullLogger<GlossaryImportTemplateService>.Instance), store);
    }

    private static UpdateGlossaryImportTemplateDto FromDefaults(Action<List<UpdateGlossaryImportTemplateColumnDto>>? edit = null)
    {
        var columns = GlossaryImportTemplateDefaults.Columns
            .Select(c => new UpdateGlossaryImportTemplateColumnDto
            {
                Key = c.Key,
                Group = c.Group,
                Order = c.Order,
                Hidden = c.Hidden,
                Name = c.Name,
                Aliases = c.Aliases.Select(a => (string?)a).ToList(),
            })
            .ToList();
        edit?.Invoke(columns);
        return new UpdateGlossaryImportTemplateDto
        {
            Columns = columns,
            Samples = GlossaryImportTemplateDefaults.Samples.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToDictionary(v => v.Key, v => (string?)v.Value)),
        };
    }

    private static string[] KeysIn(GlossaryImportTemplateDto dto, string group)
        => dto.Columns.Where(c => c.Group == group).OrderBy(c => c.Order).Select(c => c.Key).ToArray();

    [Fact]
    public async Task With_nothing_saved_the_PO_default_groups_are_served()
    {
        var (service, _) = Create();

        var result = await service.GetAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsDefault);
        Assert.Equal(new[] { "sourceTerm", "context", "partOfSpeech" }, KeysIn(result.Value, "source"));
        Assert.Equal(new[] { "targetTerm", "usageNote" }, KeysIn(result.Value, "target"));
        Assert.Equal(new[] { "domain", "definition", "priority" }, KeysIn(result.Value, "general"));
        Assert.Equal(new[] { "Term", "Context", "Part of speech", "Translation", "Note", "Field", "Definition", "Priority" },
            result.Value.Columns.Select(c => c.Name).ToArray());
        Assert.Contains("en", result.Value.Samples.Keys);
        Assert.Contains("vi", result.Value.Samples.Keys);
        Assert.Contains("ja", result.Value.Samples.Keys);
    }

    [Fact]
    public async Task The_default_is_itself_a_valid_configuration()
    {
        var (service, _) = Create();

        var result = await service.UpdateAsync(FromDefaults(), Admin);

        Assert.True(result.IsSuccess, result.Error);
    }

    [Fact]
    public async Task A_saved_configuration_is_what_every_reader_gets_afterwards()
    {
        var (service, store) = Create();
        var request = FromDefaults(columns =>
        {
            var note = columns.Single(c => c.Key == "usageNote");
            note.Name = "  Usage   note ";
            note.Group = "general";
            note.Order = -1; // first in General
            columns.Single(c => c.Key == "partOfSpeech").Hidden = true;
        });
        request.Samples!["ko"] = new Dictionary<string, string?> { ["sourceTerm"] = "청구서", ["context"] = "  " };

        var saved = await service.UpdateAsync(request, Admin);
        var read = await service.GetAsync();

        Assert.True(saved.IsSuccess, saved.Error);
        Assert.False(read.Value!.IsDefault);
        Assert.Equal(Admin, read.Value.UpdatedBy);
        Assert.Equal(new[] { "usageNote", "domain", "definition", "priority" }, KeysIn(read.Value, "general"));
        Assert.Equal(new[] { 0, 1, 2, 3 }, read.Value.Columns.Where(c => c.Group == "general").Select(c => c.Order).ToArray());
        Assert.Equal("Usage note", read.Value.Columns.Single(c => c.Key == "usageNote").Name);
        Assert.True(read.Value.Columns.Single(c => c.Key == "partOfSpeech").Hidden);
        // Blank sample values are dropped rather than stored as "".
        Assert.Equal(new[] { "sourceTerm" }, read.Value.Samples["ko"].Keys.ToArray());
        Assert.NotNull(store.Row);
    }

    [Fact]
    public async Task Reset_serves_the_default_again()
    {
        var (service, store) = Create();
        await service.UpdateAsync(FromDefaults(c => c.Single(x => x.Key == "domain").Name = "Domain"), Admin);

        var reset = await service.ResetAsync();

        Assert.True(reset.Value!.IsDefault);
        Assert.Null(store.Row);
        Assert.Equal("Field", (await service.GetAsync()).Value!.Columns.Single(c => c.Key == "domain").Name);
    }

    public static IEnumerable<object[]> Refusals()
    {
        yield return [(Action<UpdateGlossaryImportTemplateDto>)(r => r.Columns!.Single(c => c.Key == "sourceTerm").Hidden = true), "cannot be hidden"];
        yield return [(Action<UpdateGlossaryImportTemplateDto>)(r => r.Columns!.Single(c => c.Key == "targetTerm").Hidden = true), "cannot be hidden"];
        yield return [(Action<UpdateGlossaryImportTemplateDto>)(r => r.Columns!.Single(c => c.Key == "sourceTerm").Group = "general"), "Source group"];
        yield return [(Action<UpdateGlossaryImportTemplateDto>)(r => r.Columns!.Single(c => c.Key == "targetTerm").Group = "source"), "Target group"];
        yield return [(Action<UpdateGlossaryImportTemplateDto>)(r => r.Columns!.Single(c => c.Key == "context").Group = "middle"), "unknown group"];
        yield return [(Action<UpdateGlossaryImportTemplateDto>)(r => r.Columns!.Single(c => c.Key == "context").Name = " "), "needs a name"];
        yield return [(Action<UpdateGlossaryImportTemplateDto>)(r => r.Columns!.RemoveAll(c => c.Key == "priority")), "Missing column"];
        yield return [(Action<UpdateGlossaryImportTemplateDto>)(r => r.Columns!.Single(c => c.Key == "priority").Key = "attributes"), "Unknown column key"];
        yield return [(Action<UpdateGlossaryImportTemplateDto>)(r => r.Columns!.Single(c => c.Key == "priority").Key = "domain"), "appears twice"];
        // One header text, one column — "Note (Japanese)" normalizes onto Note's own name.
        yield return [(Action<UpdateGlossaryImportTemplateDto>)(r => r.Columns!.Single(c => c.Key == "definition").Aliases!.Add("Note (Japanese)")), "used by both"];
        yield return [(Action<UpdateGlossaryImportTemplateDto>)(r => r.Samples!["english"] = new() { ["sourceTerm"] = "x" }), "not a language code"];
        yield return [(Action<UpdateGlossaryImportTemplateDto>)(r => r.Samples!["en"]["priority"] = "high"), "whole number"];
        yield return [(Action<UpdateGlossaryImportTemplateDto>)(r => r.Samples!["en"]["reading"] = "x"), "unknown column"];
        yield return [(Action<UpdateGlossaryImportTemplateDto>)(r => r.Samples!["EN-us"] = new() { ["sourceTerm"] = "x" }), "given twice"];
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task An_invalid_configuration_is_refused_and_nothing_is_saved(Action<UpdateGlossaryImportTemplateDto> breakIt, string expected)
    {
        var (service, store) = Create();
        var request = FromDefaults();
        breakIt(request);

        var result = await service.UpdateAsync(request, Admin);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Contains(expected, result.Error);
        Assert.Null(store.Row);
    }

    [Fact]
    public async Task A_column_missing_from_an_older_saved_configuration_comes_back_with_its_default()
    {
        var (service, store) = Create();
        store.Row = new GlossaryImportTemplate
        {
            Config = """{"version":1,"columns":[{"key":"sourceTerm","group":"source","order":0,"hidden":false,"name":"Word","aliases":[]},{"key":"targetTerm","group":"target","order":0,"hidden":false,"name":"Meaning","aliases":[]}],"samples":{"en":{"sourceTerm":"hello"}}}""",
            UpdatedAt = DateTime.UtcNow,
        };

        var read = (await service.GetAsync()).Value!;

        Assert.False(read.IsDefault);
        Assert.Equal("Word", read.Columns.Single(c => c.Key == "sourceTerm").Name);
        Assert.Equal(GlossaryImportTemplateColumnKeys.All.Length, read.Columns.Count);
        Assert.Equal(new[] { "sourceTerm", "context", "partOfSpeech" }, KeysIn(read, "source"));
        Assert.Equal("hello", read.Samples["en"]["sourceTerm"]);
    }

    [Fact]
    public async Task An_unreadable_stored_configuration_falls_back_to_the_default()
    {
        var (service, store) = Create();
        store.Row = new GlossaryImportTemplate { Config = "not json", UpdatedAt = DateTime.UtcNow };

        var read = (await service.GetAsync()).Value!;

        Assert.True(read.IsDefault);
        Assert.Equal("Term", read.Columns[0].Name);
    }

    [Theory]
    [InlineData("Translation (Vietnamese)", "translation")]
    [InlineData("  Part   of Speech ", "part of speech")]
    [InlineData("Term", "term")]
    [InlineData("(English)", "(english)")]
    public void A_header_cell_is_compared_without_its_language_suffix(string cell, string expected)
    {
        Assert.Equal(expected, GlossaryImportTemplateRules.NormalizeHeader(cell));
    }
}

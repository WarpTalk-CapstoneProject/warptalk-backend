using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSubstitute;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// 3 Oct 2026: Notion synced a tool whose input schema nests 32 levels. Wrapped in the catalog
/// response it passed ASP.NET Core's MaxDepth of 32, and GET /plugins broke mid-write for every
/// user - "Could not load plugins" - while WarpBot's /mcp/tools carried the same schema.
/// </summary>
public class ToolSchemaDepthTests
{
    /// <summary>A JSON-schema-shaped object that nests <paramref name="levels"/> containers.</summary>
    private static JsonObject NestedSchema(int levels)
    {
        // Each step is an object property holding the next schema: properties -> name -> schema.
        JsonObject inner = new() { ["type"] = "string", ["description"] = "leaf" };
        var depth = 1;
        while (depth + 2 <= levels)
        {
            inner = new JsonObject
            {
                ["type"] = "object",
                ["description"] = $"level {depth}",
                ["properties"] = new JsonObject { ["child"] = inner },
            };
            depth += 2;
        }
        return inner;
    }

    [Fact]
    public void A_schema_within_the_limit_is_returned_unchanged()
    {
        var schema = NestedSchema(8);

        var clamped = ToolSchemaDepth.Clamp(schema);

        Assert.Same(schema, clamped);
    }

    [Fact]
    public void A_deep_schema_is_cut_to_the_limit_and_keeps_its_scalar_members()
    {
        var schema = NestedSchema(33);
        Assert.True(ToolSchemaDepth.DepthOf(schema) > 30);

        var clamped = ToolSchemaDepth.Clamp(schema);

        Assert.True(ToolSchemaDepth.DepthOf(clamped) <= ToolSchemaDepth.MaxDepth);
        Assert.Equal("object", (string?)clamped["type"]);
        Assert.False(string.IsNullOrEmpty((string?)clamped["description"]));
        // The original is left alone: the stored manifest is not rewritten by reading it.
        Assert.True(ToolSchemaDepth.DepthOf(schema) > 30);
    }

    [Fact]
    public void A_choice_list_whose_every_branch_was_too_deep_is_dropped_not_emptied()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["anyOf"] = new JsonArray(new JsonObject { ["properties"] = new JsonObject { ["x"] = new JsonObject() } }),
        };

        var clamped = ToolSchemaDepth.Clamp(schema, maxDepth: 2);

        // `anyOf: []` would match nothing; dropping it only loosens the schema.
        Assert.False(clamped.ContainsKey("anyOf"));
        Assert.Equal("object", (string?)clamped["type"]);
    }

    [Fact]
    public void Scalar_lists_survive_inside_the_limit()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("a", "b"),
            ["properties"] = NestedSchema(30),
        };

        var clamped = ToolSchemaDepth.Clamp(schema);

        Assert.Equal(2, clamped["required"]!.AsArray().Count);
    }

    [Fact]
    public async Task The_catalog_serialises_a_deep_provider_schema_under_aspnet_core_default_depth()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var plugins = Substitute.For<IPluginRepository>();
        var installations = Substitute.For<IPluginInstallationRepository>();
        var connections = Substitute.For<IPluginConnectionRepository>();
        unitOfWork.PluginRepository.Returns(plugins);
        unitOfWork.PluginInstallationRepository.Returns(installations);
        unitOfWork.PluginConnectionRepository.Returns(connections);

        var deepTool = new
        {
            name = "notion-query-data-sources",
            pluginKey = "notion",
            label = "Query data sources",
            description = "",
            effect = "read",
            requiredScopes = Array.Empty<string>(),
            parameters = NestedSchema(33),
        };
        var notion = new Plugin
        {
            Id = Guid.NewGuid(),
            PluginKey = "notion",
            Label = "Notion",
            Description = "Notion",
            Provider = "notion",
            Kind = PluginConstants.PluginKind.Mcp,
            McpServerUrl = "https://mcp.notion.test/mcp",
            IsActive = true,
            RequiredScopesJson = "[]",
            ToolsJson = JsonSerializer.Serialize(new[] { deepTool }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        plugins.FindAsync(Arg.Any<Expression<Func<Plugin, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([notion]);
        installations.FindAsync(Arg.Any<Expression<Func<PluginInstallation, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        connections.FindAsync(Arg.Any<Expression<Func<PluginConnection, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = new PluginInstallationService(
            unitOfWork,
            Substitute.For<IPluginCredentialProtector>(),
            TestWorkspacePluginPolicy.Guard(true, isActiveMember: true, "Member"),
            Substitute.For<IAdminAuditRecorder>());

        var result = await sut.ListCatalogAsync(Guid.NewGuid());

        Assert.True(result.IsSuccess);
        var tool = Assert.Single(Assert.Single(result.Value!).Tools);
        Assert.True(ToolSchemaDepth.DepthOf(tool.Parameters) <= ToolSchemaDepth.MaxDepth);

        // What MVC does with the response, at its default depth of 32 - this threw in production.
        var mvcDefault = new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 32 };
        var body = JsonSerializer.Serialize(new { items = result.Value }, mvcDefault);
        Assert.Contains("notion-query-data-sources", body);
    }
}

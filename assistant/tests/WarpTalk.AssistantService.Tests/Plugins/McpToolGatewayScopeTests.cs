using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Infrastructure.Mcp;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// What scopes a tool synced from a remote MCP server carries. WT-646.
/// </summary>
/// <remarks>
/// Worth a file of its own because the bug it guards was invisible: every descriptor came back with
/// an empty scope list, and the orchestrator's scope check compares a tool's RequiredScopes against
/// the connection's granted set. An empty list is satisfied by any grant at all, so the gate passed
/// for every MCP plugin no matter what the user had actually approved - and a passing gate looks
/// exactly like a correct one.
/// </remarks>
public class McpToolGatewayScopeTests
{
    private const string RemoteKey = "remote_app";
    private const string ReadScope = "https://remote.test/auth/read";
    private const string WriteScope = "https://remote.test/auth/write";

    [Fact]
    public async Task ListToolsAsync_GivesEveryToolTheRowsDeclaredScopes()
    {
        var sut = GatewayAnswering(new JsonArray
        {
            new JsonObject
            {
                ["name"] = "remote_app_search",
                ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
            },
            new JsonObject { ["name"] = "remote_app_create" },
        });

        var tools = await sut.ListToolsAsync(Definition(ReadScope, WriteScope), Connected());

        Assert.Equal(2, tools.Count);
        Assert.All(tools, tool => Assert.Equal([ReadScope, WriteScope], tool.RequiredScopes));

        // The effect still comes from the server's own annotation - this change is about scopes and
        // must not disturb the confirmation gate, which reads Effect.
        Assert.Equal(PluginConstants.ToolEffect.Read, tools.Single(t => t.Name == "remote_app_search").Effect);
        Assert.Equal(PluginConstants.ToolEffect.Write, tools.Single(t => t.Name == "remote_app_create").Effect);
    }

    [Fact]
    public async Task ListToolsAsync_LeavesScopesEmpty_WhenTheRowDeclaresNone()
    {
        // A row that asks for nothing is a row whose tools need nothing. Inventing a scope here
        // would refuse a plugin that works, which is the opposite failure and just as wrong.
        var sut = GatewayAnswering(new JsonArray { new JsonObject { ["name"] = "remote_app_ping" } });

        var tools = await sut.ListToolsAsync(Definition(), Connected());

        Assert.Empty(Assert.Single(tools).RequiredScopes);
    }

    private static McpToolGateway GatewayAnswering(JsonArray tools)
    {
        // Two calls go out - initialize, then tools/list - and both are answered from the same
        // envelope shape, so the handler does not need to tell them apart.
        var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "1",
                    ["result"] = new JsonObject { ["tools"] = tools.DeepClone() },
                }),
            }));

        var protector = Substitute.For<IPluginCredentialProtector>();
        protector.Unprotect("enc:access").Returns("access-token");

        return new McpToolGateway(httpClient, protector, NullLogger<McpToolGateway>.Instance);
    }

    private static PluginDefinitionDto Definition(params string[] scopes) =>
        new(
            Id: Guid.NewGuid(),
            Key: RemoteKey,
            Provider: RemoteKey,
            Label: "Remote App",
            Description: "A remote MCP server.",
            AvatarUrl: null,
            RequiredScopes: scopes,
            Tools: Array.Empty<McpToolDescriptorDto>(),
            Kind: PluginConstants.PluginKind.Mcp,
            McpServerUrl: "https://remote.test/mcp",
            IsFeatured: false,
            SortOrder: 0,
            Category: null);

    private static PluginConnection Connected() =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PluginId = Guid.NewGuid(),
            Provider = RemoteKey,
            Status = PluginConstants.ConnectionStatus.Connected,
            EncryptedAccessToken = "enc:access",
        };

    private class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}

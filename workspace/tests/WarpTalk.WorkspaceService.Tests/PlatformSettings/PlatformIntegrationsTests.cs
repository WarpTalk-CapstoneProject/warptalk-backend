using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using WarpTalk.Shared.PlatformSettings;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Infrastructure.Clients;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests.PlatformSettings;

/// <summary>
/// The Integrations section: each service's self-report is assembled per integration, configured
/// only when every reporter has it, and what a service writes carries booleans and non-secret
/// details — never a configured value.
/// </summary>
public sealed class PlatformIntegrationsTests
{
    [Fact]
    public void Reports_from_every_service_and_the_ai_workers_are_assembled_per_integration()
    {
        var now = DateTimeOffset.UtcNow;
        var reports = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["billing"] = Fields(IntegrationStatusReporter.Entries(IntegrationStatusServiceCollectionExtensions.Snapshot(
                (IntegrationKeys.Stripe, new IntegrationReport(true, "checkout and webhooks")),
                (IntegrationKeys.ObjectStorage, new IntegrationReport(false, "expense receipts · Local"))), now)),
            ["workspace"] = Fields(IntegrationStatusReporter.Entries(IntegrationStatusServiceCollectionExtensions.Snapshot(
                (IntegrationKeys.ObjectStorage, new IntegrationReport(true, "knowledge documents · S3"))), now)),
            // Written by warptalk-ai shared/integration_status.py.
            ["ai-tts"] = new Dictionary<string, string>
            {
                ["cartesia"] = "{\"configured\": true, \"detail\": \"tts model sonic-3.5\", \"reportedAt\": \"2026-09-25T10:00:00Z\"}",
                ["unknown_vendor"] = "{\"configured\": true}",
                ["livekit"] = "not json",
            },
            ["gateway"] = Fields(IntegrationStatusReporter.Entries(new IntegrationStatusSnapshot(
                new Dictionary<string, IntegrationReport> { [IntegrationKeys.Redis] = new(true, null) },
                [new DeployConfigReport("cors.allowed_origins", "CORS allowed origins", ["https://warptalk.vn"])]), now)),
        };
        var checks = new Dictionary<string, string>
        {
            [IntegrationKeys.Redis] = JsonSerializer.Serialize(new IntegrationCheckDto(true, now, 2, "PONG in 1 ms"), IntegrationStatusReporter.Json),
        };

        var view = PlatformIntegrationsService.Assemble(reports, checks);

        Assert.Equal(IntegrationKeys.All.Select(d => d.Key), view.Integrations.Select(i => i.Key));
        var stripe = view.Integrations.Single(i => i.Key == IntegrationKeys.Stripe);
        Assert.True(stripe.Configured);
        Assert.Equal("/admin/providers", stripe.Href);
        var storage = view.Integrations.Single(i => i.Key == IntegrationKeys.ObjectStorage);
        Assert.False(storage.Configured); // billing still stores receipts on local disk
        Assert.Equal(2, storage.Services.Count);
        Assert.True(view.Integrations.Single(i => i.Key == IntegrationKeys.Cartesia).Configured);
        Assert.Null(view.Integrations.Single(i => i.Key == IntegrationKeys.LiveKit).Configured); // malformed = not reported
        Assert.Null(view.Integrations.Single(i => i.Key == IntegrationKeys.GoogleOAuth).Configured);
        var redis = view.Integrations.Single(i => i.Key == IntegrationKeys.Redis);
        Assert.True(redis.Testable);
        Assert.True(redis.LastCheck!.Ok);
        var cors = Assert.Single(view.DeployConfig);
        Assert.Equal("gateway", cors.Service);
        Assert.Equal(["https://warptalk.vn"], cors.Value);
    }

    [Fact]
    public void A_report_never_carries_a_configured_value()
    {
        const string secret = "sk_live_should_never_leave_the_pod";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stripe:SecretKey"] = secret,
            ["Stripe:WebhookSecret"] = "whsec_also_secret",
            ["Storage:Provider"] = "S3",
            ["Storage:S3:AccessKey"] = "AKIA_SECRET",
            ["Storage:S3:SecretKey"] = "s3-secret",
            ["Storage:S3:BucketName"] = "bucket",
            ["Cartesia:AdminApiKey"] = "CHANGE_ME",
        }).Build();

        var snapshot = IntegrationStatusServiceCollectionExtensions.Snapshot(
            (IntegrationKeys.Stripe, IntegrationReport.FromConfiguration(configuration, "checkout", "Stripe:SecretKey", "Stripe:WebhookSecret")),
            (IntegrationKeys.Cartesia, IntegrationReport.FromConfiguration(configuration, "usage sync", "Cartesia:AdminApiKey")),
            (IntegrationKeys.ObjectStorage, IntegrationReport.ObjectStorage(configuration, "receipts")));
        var written = string.Join("\n", IntegrationStatusReporter.Entries(snapshot, DateTimeOffset.UtcNow).Select(e => e.Value.ToString()));

        Assert.DoesNotContain(secret, written);
        Assert.DoesNotContain("whsec_", written);
        Assert.DoesNotContain("AKIA", written);
        Assert.True(snapshot.Integrations[IntegrationKeys.Stripe].Configured);
        Assert.False(snapshot.Integrations[IntegrationKeys.Cartesia].Configured); // a placeholder is not configured
        Assert.True(snapshot.Integrations[IntegrationKeys.ObjectStorage].Configured);
    }

    private static IReadOnlyDictionary<string, string> Fields(StackExchange.Redis.HashEntry[] entries)
        => entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
}

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.Entitlements;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.BackgroundServices;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// WT-263: propagation. A change in BillingService has to become a local snapshot here, or
/// enforcement reads nothing and every workspace sits in cold start forever.
/// </summary>
public class EntitlementsChangedConsumerTests
{
    private static EventEnvelope<EntitlementsChangedEventPayload> Envelope(
        Guid workspaceId,
        string planSlug,
        string maxLanguages,
        DateTime resolvedAt,
        string source = "plan:enterprise") =>
        new(
            Guid.NewGuid(),
            BillingEventTypes.EntitlementsChanged,
            DomainEventEnvelope.CurrentSchemaVersion,
            resolvedAt,
            "billing-service",
            null,
            null,
            workspaceId.ToString(),
            new EntitlementsChangedEventPayload(
                workspaceId,
                planSlug,
                HasActiveSubscription: true,
                resolvedAt,
                "plan_changed",
                new List<ResolvedEntitlementPayload>
                {
                    new("max_languages", maxLanguages, source),
                    new("max_active_rooms", "50", source)
                }));

    private static EntitlementsChangedConsumer CreateConsumer() =>
        new(
            Substitute.For<IConnectionMultiplexer>(),
            Substitute.For<IServiceProvider>(),
            NullLogger<EntitlementsChangedConsumer>.Instance);

    [Fact]
    public async Task PlanChange_CreatesTheSnapshot_WhenTheWorkspaceHasNoneYet()
    {
        var workspaceId = Guid.NewGuid();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.WorkspaceEntitlementSnapshotRepository
            .GetForWorkspaceAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns((WorkspaceEntitlementSnapshot?)null);

        WorkspaceEntitlementSnapshot? added = null;
        await unitOfWork.WorkspaceEntitlementSnapshotRepository
            .AddAsync(Arg.Do<WorkspaceEntitlementSnapshot>(snapshot => added = snapshot), Arg.Any<CancellationToken>());

        await CreateConsumer().ApplyAsync(
            unitOfWork,
            Envelope(workspaceId, "enterprise", "3", DateTime.UtcNow),
            CancellationToken.None);

        Assert.NotNull(added);
        Assert.Equal(workspaceId, added!.WorkspaceId);
        Assert.Equal("enterprise", added.PlanSlug);
        Assert.True(added.HasActiveSubscription);

        // Stored in the shape enforcement parses.
        var entitlements = WorkspaceEntitlements.FromSnapshot(added.EntitlementsJson, added.HasActiveSubscription);
        Assert.Equal(3, entitlements.Limit(EntitlementKeys.MaxLanguages));
        Assert.Equal("plan:enterprise", entitlements.Source(EntitlementKeys.MaxLanguages));

        await unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlanChange_UpdatesAnExistingSnapshot()
    {
        var workspaceId = Guid.NewGuid();
        var existing = new WorkspaceEntitlementSnapshot
        {
            WorkspaceId = workspaceId,
            EntitlementsJson = "{\"max_languages\":{\"value\":\"2\",\"source\":\"plan:startup\"}}",
            PlanSlug = "startup",
            HasActiveSubscription = true,
            ResolvedAt = DateTime.UtcNow.AddHours(-1)
        };

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.WorkspaceEntitlementSnapshotRepository
            .GetForWorkspaceAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(existing);

        await CreateConsumer().ApplyAsync(
            unitOfWork,
            Envelope(workspaceId, "enterprise", "3", DateTime.UtcNow),
            CancellationToken.None);

        Assert.Equal("enterprise", existing.PlanSlug);
        var entitlements = WorkspaceEntitlements.FromSnapshot(existing.EntitlementsJson, existing.HasActiveSubscription);
        Assert.Equal(3, entitlements.Limit(EntitlementKeys.MaxLanguages));
    }

    /// <summary>
    /// Delivery is at-least-once and unordered. A REPLAYED older event must not roll a workspace
    /// back onto a plan it has already left — the snapshot would otherwise flap between plans every
    /// time the dispatcher retried.
    /// </summary>
    [Fact]
    public async Task StaleEvent_IsIgnored_SoARedeliveryCannotRollTheWorkspaceBack()
    {
        var workspaceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var existing = new WorkspaceEntitlementSnapshot
        {
            WorkspaceId = workspaceId,
            EntitlementsJson = "{\"max_languages\":{\"value\":\"3\",\"source\":\"plan:enterprise\"}}",
            PlanSlug = "enterprise",
            HasActiveSubscription = true,
            ResolvedAt = now
        };

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.WorkspaceEntitlementSnapshotRepository
            .GetForWorkspaceAsync(workspaceId, Arg.Any<CancellationToken>())
            .Returns(existing);

        await CreateConsumer().ApplyAsync(
            unitOfWork,
            Envelope(workspaceId, "startup", "2", now.AddMinutes(-30), "plan:startup"),
            CancellationToken.None);

        Assert.Equal("enterprise", existing.PlanSlug);
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Envelope_IsRejected_WhenItIsNotTheEventOrSchemaWeUnderstand()
    {
        var good = JsonSerializer.Serialize(Envelope(Guid.NewGuid(), "enterprise", "3", DateTime.UtcNow));
        Assert.True(EntitlementsChangedConsumer.TryParseEnvelope(good, out var parsed));
        Assert.NotNull(parsed);

        Assert.False(EntitlementsChangedConsumer.TryParseEnvelope("{not json", out _));
        Assert.False(EntitlementsChangedConsumer.TryParseEnvelope("{\"event_type\":\"billing.payment_succeeded\"}", out _));
    }
}

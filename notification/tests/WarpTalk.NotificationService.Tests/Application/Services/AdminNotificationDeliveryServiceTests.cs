using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.NotificationService.Application.DTOs.AdminNotifications;
using WarpTalk.NotificationService.Application.Mappers;
using WarpTalk.NotificationService.Application.Services;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.NotificationService.Tests.Application.Services;

/// <summary>
/// Status used to stay "Pending" forever: the consumer wrote every recipient's row and never
/// touched the announcement. These pin what a processed chunk now does to it.
/// </summary>
public class AdminNotificationDeliveryServiceTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IAdminNotificationRepository> _announcements = new();
    private readonly Mock<INotificationMessageRepository> _messages = new();
    private readonly Mock<INotificationInboxMessageRepository> _inbox = new();
    private readonly List<string> _calls = new();
    private readonly AdminNotificationDeliveryService _sut;

    private readonly AdminNotification _announcement = new()
    {
        Id = Guid.NewGuid(),
        Title = "Maintenance tonight",
        Content = "c",
        Type = NotificationConstants.TypeSystem,
        Payload = "{}",
        TargetAudienceMode = NotificationConstants.TargetModeSpecificUsers,
        TargetAudienceData = "{}",
        Status = NotificationConstants.StatusPending,
        DeliveryChunkCount = 2
    };

    public AdminNotificationDeliveryServiceTests()
    {
        _unitOfWork.Setup(u => u.AdminNotificationRepository).Returns(_announcements.Object);
        _unitOfWork.Setup(u => u.NotificationMessageRepository).Returns(_messages.Object);
        _unitOfWork.Setup(u => u.NotificationInboxMessageRepository).Returns(_inbox.Object);
        _unitOfWork.Setup(u => u.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("begin")).Returns(Task.CompletedTask);
        _unitOfWork.Setup(u => u.SaveChangesAsync())
            .Callback(() => _calls.Add("save")).ReturnsAsync(1);
        _unitOfWork.Setup(u => u.CommitTransactionAsync(It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("commit")).Returns(Task.CompletedTask);
        _unitOfWork.Setup(u => u.RollbackTransactionAsync(It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("rollback")).Returns(Task.CompletedTask);
        _announcements.Setup(r => r.GetByIdAsync(_announcement.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_announcement);
        _announcements.Setup(r => r.RecordChunkDeliveredAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("record")).Returns(Task.CompletedTask);

        _sut = new AdminNotificationDeliveryService(
            _unitOfWork.Object,
            NullLogger<AdminNotificationDeliveryService>.Instance);
    }

    private DeliveryEventPayload Chunk(params Guid[] userIds) =>
        new(_announcement.Id, NotificationConstants.TargetModeSpecificUsers, userIds);

    [Fact]
    public async Task DeliverChunkAsync_CountsTheChunkAndItsDistinctRecipientsInsideTheTransaction()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        var written = await _sut.DeliverChunkAsync(Chunk(alice, bob, alice), Guid.NewGuid());

        Assert.Equal(2, written.Count);
        _announcements.Verify(r => r.RecordChunkDeliveredAsync(_announcement.Id, 2, It.IsAny<CancellationToken>()), Times.Once);
        // The counter moves only after the rows and the receipt are flushed, and before commit,
        // so a chunk cannot be counted without its rows or its rows exist uncounted.
        Assert.Equal(new[] { "begin", "save", "record", "commit" }, _calls);
    }

    /// <summary>
    /// WT-699 / TC4104: a BROADCAST / SEGMENT chunk arrives with its recipients already resolved,
    /// and used to throw "Unsupported admin notification audience mode" here.
    /// </summary>
    [Theory]
    [InlineData(NotificationConstants.TargetModeBroadcast)]
    [InlineData(NotificationConstants.TargetModeSegment)]
    public async Task DeliverChunkAsync_DeliversResolvedBroadcastAndSegmentChunks(string mode)
    {
        var recipient = Guid.NewGuid();

        var written = await _sut.DeliverChunkAsync(
            new DeliveryEventPayload(_announcement.Id, mode, new[] { recipient }), Guid.NewGuid());

        Assert.Single(written);
    }

    [Fact]
    public async Task DeliverChunkAsync_WhenAlreadyProcessed_DoesNotCountTheChunkAgain()
    {
        var eventId = Guid.NewGuid();
        _inbox.Setup(i => i.HasProcessedAsync(eventId, AdminNotificationDeliveryService.InboxConsumerName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var written = await _sut.DeliverChunkAsync(Chunk(Guid.NewGuid()), eventId);

        Assert.Empty(written);
        _announcements.Verify(r => r.RecordChunkDeliveredAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task DeliverChunkAsync_WhenTheWriteFails_RollsBackWithoutCounting()
    {
        _unitOfWork.Setup(u => u.SaveChangesAsync())
            .Callback(() => _calls.Add("save"))
            .ThrowsAsync(new DbUpdateException("duplicate inbox receipt"));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => _sut.DeliverChunkAsync(Chunk(Guid.NewGuid()), Guid.NewGuid()));

        Assert.Equal(new[] { "begin", "save", "rollback" }, _calls);
    }

    [Fact]
    public async Task DeliverChunkAsync_WhenAnnouncementIsMissing_ThrowsSoTheEventIsRetried()
    {
        var payload = new DeliveryEventPayload(Guid.NewGuid(), NotificationConstants.TargetModeSpecificUsers, [Guid.NewGuid()]);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _sut.DeliverChunkAsync(payload, Guid.NewGuid()));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task MarkDeliveryFailedAsync_MarksTheAnnouncementFailed()
    {
        _announcements.Setup(r => r.MarkFailedAsync(_announcement.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await _sut.MarkDeliveryFailedAsync(_announcement.Id);

        _announcements.Verify(r => r.MarkFailedAsync(_announcement.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Mapper_ExposesSentAtAndDeliveredCountOnListAndDetail()
    {
        var sentAt = new DateTime(2026, 9, 16, 9, 0, 0, DateTimeKind.Utc);
        _announcement.Status = NotificationConstants.StatusSent;
        _announcement.SentAt = sentAt;
        _announcement.DeliveredCount = 1500;

        var summary = AdminNotificationMapper.ToSummaryDto(_announcement);
        var detail = AdminNotificationMapper.ToDetailDto(_announcement);

        Assert.Equal((NotificationConstants.StatusSent, sentAt, 1500), (summary.Status, summary.SentAt, summary.DeliveredCount));
        Assert.Equal((NotificationConstants.StatusSent, sentAt, 1500), (detail.Status, detail.SentAt, detail.DeliveredCount));
    }

    /// <summary>
    /// NotificationDbContext hand-maps every column. A property without HasColumnName is sent to
    /// Postgres in PascalCase and 500s every SELECT over the table — the admin list included.
    /// </summary>
    [Theory]
    [InlineData(nameof(AdminNotification.SentAt), "sent_at")]
    [InlineData(nameof(AdminNotification.DeliveredCount), "delivered_count")]
    [InlineData(nameof(AdminNotification.DeliveryChunkCount), "delivery_chunk_count")]
    [InlineData(nameof(AdminNotification.DeliveredChunkCount), "delivered_chunk_count")]
    public void DeliveryColumns_AreMappedToTheMigrationsColumnNames(string property, string column)
    {
        using var context = new NotificationDbContext(
            new DbContextOptionsBuilder<NotificationDbContext>()
                .UseNpgsql("Host=unused;Database=unused")
                .Options);

        var entity = context.Model.FindEntityType(typeof(AdminNotification))!;

        Assert.Equal(column, entity.FindProperty(property)!.GetColumnName());
    }
}

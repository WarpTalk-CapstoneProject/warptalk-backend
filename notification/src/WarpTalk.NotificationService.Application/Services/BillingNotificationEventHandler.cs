using System.Text.Json;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared.Events;
using WarpTalk.Shared.Models;

namespace WarpTalk.NotificationService.Application.Services;

public sealed class BillingNotificationEventHandler(
    IUnitOfWork unitOfWork,
    IMessagePublisher messagePublisher)
{
    public const string ConsumerName = "notification-service.billing-events.v1";

    public async Task<bool> HandleAsync(OutboxEventMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsSupported(message.EventType))
            return false;

        var inboxRepository = unitOfWork.NotificationInboxMessageRepository;
        if (await inboxRepository.HasProcessedAsync(message.EventId, ConsumerName, cancellationToken))
            return false;

        var envelope = JsonSerializer.Deserialize<EventEnvelope<BillingPaymentEventPayload>>(
            message.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Billing event {message.EventId} has an invalid payload.");
        if (!Guid.TryParse(envelope.Payload.UserId, out var userId))
            throw new InvalidOperationException($"Billing event {message.EventId} has an invalid user id.");

        var (type, title, content) = NotificationCopy(message.EventType, envelope.Payload);
        var notification = new NotificationMessage
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = type,
            Title = title,
            Content = content,
            ActionUrl = "/workspace/payment",
            PayloadJson = message.PayloadJson,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };
        await unitOfWork.NotificationMessageRepository.AddAsync(notification);
        await inboxRepository.AddAsync(new NotificationInboxMessage
        {
            EventId = message.EventId,
            Consumer = ConsumerName,
            EventType = message.EventType,
            ProcessedAt = DateTime.UtcNow
        });

        await unitOfWork.SaveChangesAsync();
        await messagePublisher.PublishAsync(
            "warptalk:notifications:new",
            new RealtimeNotificationMessage
            {
                Id = notification.Id.ToString(),
                UserId = notification.UserId.ToString(),
                Type = notification.Type,
                Title = notification.Title,
                Content = notification.Content,
                ActionUrl = notification.ActionUrl,
                PayloadJson = notification.PayloadJson,
                CreatedAt = notification.CreatedAt.ToString("O")
            },
            cancellationToken);
        return true;
    }

    private static bool IsSupported(string eventType) =>
        eventType is BillingEventTypes.PaymentSucceeded
            or BillingEventTypes.PaymentFailed
            or BillingEventTypes.PaymentRefunded
            or BillingEventTypes.PaymentDisputed;

    private static (string Type, string Title, string Content) NotificationCopy(
        string eventType,
        BillingPaymentEventPayload payload) => eventType switch
        {
            BillingEventTypes.PaymentSucceeded => (
                "BILLING_PAYMENT_SUCCEEDED",
                "Payment successful",
                $"Your {payload.PlanSlug} subscription payment was completed."),
            BillingEventTypes.PaymentFailed => (
                "BILLING_PAYMENT_FAILED",
                "Payment failed",
                payload.FailureReason ?? "Your subscription payment could not be completed."),
            BillingEventTypes.PaymentRefunded => (
                "BILLING_PAYMENT_REFUNDED",
                "Payment refunded",
                "Your payment refund has been processed."),
            BillingEventTypes.PaymentDisputed => (
                "BILLING_PAYMENT_DISPUTED",
                "Payment disputed",
                "A dispute was opened for your payment."),
            _ => throw new InvalidOperationException($"Unsupported Billing event type: {eventType}")
        };
}

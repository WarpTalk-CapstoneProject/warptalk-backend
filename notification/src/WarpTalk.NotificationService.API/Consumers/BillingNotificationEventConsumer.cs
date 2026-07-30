using MassTransit;
using WarpTalk.NotificationService.Application.Services;
using WarpTalk.Shared.Events;

namespace WarpTalk.NotificationService.API.Consumers;

public sealed class BillingNotificationEventConsumer(BillingNotificationEventHandler handler)
    : IConsumer<OutboxEventMessage>
{
    public Task Consume(ConsumeContext<OutboxEventMessage> context)
        => handler.HandleAsync(context.Message, context.CancellationToken);
}

public sealed class BillingNotificationEventConsumerDefinition
    : ConsumerDefinition<BillingNotificationEventConsumer>
{
    public BillingNotificationEventConsumerDefinition()
    {
        EndpointName = "notification-billing-events-v1";
        ConcurrentMessageLimit = 8;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<BillingNotificationEventConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(retry => retry.Intervals(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(2)));
    }
}

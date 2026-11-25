using Azure.Messaging.ServiceBus;

namespace Orders.Infrastructure.Messaging;

public sealed class OrderEventPublisher(ServiceBusClient client)
{
    private readonly ServiceBusSender _sender = client.CreateSender("order-events");

    public Task PublishAsync(string body, CancellationToken ct) => _sender.SendMessageAsync(new ServiceBusMessage(body), ct);
}

using Microsoft.Azure.ServiceBus;

namespace Billing.Web.Messaging;

public class InvoiceListener
{
    private readonly QueueClient _client = new("Endpoint=sb://legacy/", "invoices");

    public void Start() => _client.RegisterMessageHandler((m, ct) => Task.CompletedTask, e => Task.CompletedTask);
}

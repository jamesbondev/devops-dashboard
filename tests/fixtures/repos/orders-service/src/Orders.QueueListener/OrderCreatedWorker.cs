using Azure.Messaging.ServiceBus;

namespace Orders.QueueListener;

public sealed class OrderCreatedWorker(ServiceBusClient client, ILogger<OrderCreatedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processor = client.CreateProcessor("orders-created", new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 4,
        });
        processor.ProcessMessageAsync += async args =>
        {
            try
            {
                logger.LogInformation("Order {Id}", args.Message.MessageId);
                await args.CompleteMessageAsync(args.Message, stoppingToken);
            }
            catch (FormatException ex)
            {
                await args.DeadLetterMessageAsync(args.Message, "InvalidPayload", ex.Message, stoppingToken);
            }
        };
        processor.ProcessErrorAsync += _ => Task.CompletedTask;
        await processor.StartProcessingAsync(stoppingToken);
    }
}

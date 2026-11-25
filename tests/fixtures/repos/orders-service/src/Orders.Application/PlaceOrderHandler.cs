using Orders.Domain;

namespace Orders.Application;

public interface IOrderRepository
{
    Task AddAsync(Order order, CancellationToken ct);
    Task<Order?> GetAsync(Guid id, CancellationToken ct);
}

public sealed class PlaceOrderHandler(IOrderRepository repository)
{
    public async Task<Guid> HandleAsync(string customerId, CancellationToken ct)
    {
        var order = new Order { CustomerId = customerId };
        await repository.AddAsync(order, ct);
        return order.Id;
    }
}

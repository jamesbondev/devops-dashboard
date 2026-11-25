using Microsoft.EntityFrameworkCore;
using Orders.Application;
using Orders.Domain;

namespace Orders.Infrastructure.Persistence;

internal sealed class OrderRepository(OrdersDbContext db) : IOrderRepository
{
    public async Task AddAsync(Order order, CancellationToken ct)
    {
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
    }

    public Task<Order?> GetAsync(Guid id, CancellationToken ct) => db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);
}

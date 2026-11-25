namespace Orders.Domain;

public sealed class Order
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string CustomerId { get; init; } = "";
    public decimal Total { get; private set; }
    public OrderStatus Status { get; private set; } = OrderStatus.Pending;

    public void Confirm() => Status = OrderStatus.Confirmed;
}

public enum OrderStatus { Pending, Confirmed, Cancelled }

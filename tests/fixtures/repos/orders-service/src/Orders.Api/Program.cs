using Orders.Application;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
var app = builder.Build();

app.MapPost("/orders", async (string customerId, PlaceOrderHandler handler, CancellationToken ct)
    => Results.Ok(await handler.HandleAsync(customerId, ct)));
app.MapHealthChecks("/health/ready");
app.MapHealthChecks("/health/live");
app.Run();

public partial class Program;

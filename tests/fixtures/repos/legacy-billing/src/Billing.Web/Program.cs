var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => "billing");
app.Run();

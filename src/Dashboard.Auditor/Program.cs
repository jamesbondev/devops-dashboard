using Dashboard.Auditor;
using Dashboard.Auditor.Engines;
using Dashboard.Auditor.Jobs;
using Dashboard.Auditor.Security;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"]))
{
    builder.WebHost.UseUrls("http://0.0.0.0:8081");
}

// Options are resolved after Build() so test hosts can override configuration.
builder.Services.AddSingleton(sp => AuditorOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AuditJobStore>();
builder.Services.AddSingleton<MockAuditEngine>();
builder.Services.AddSingleton<CopilotAuditEngine>();
builder.Services.TryAddSingleton<IAuditEngine>(sp =>
    sp.GetRequiredService<AuditorOptions>().IsCopilot
        ? sp.GetRequiredService<CopilotAuditEngine>()
        : sp.GetRequiredService<MockAuditEngine>());
builder.Services.AddHostedService<AuditWorker>();

var app = builder.Build();

// Fails startup on invalid configuration (e.g. copilot engine without AUDITOR_TOKEN).
var options = app.Services.GetRequiredService<AuditorOptions>();
if (options.TokenWasDefaulted)
{
    app.Logger.LogWarning("AUDITOR_TOKEN is not set; using the development token '{Token}'. Set AUDITOR_TOKEN for anything but local testing.", AuditorOptions.DevToken);
}
app.Logger.LogInformation("Auditor starting: engine={Engine}, allowedRoot={Root}, tools={Tools}, copilotHome={Home}",
    options.Engine, options.AllowedRoot, string.Join(",", options.Tools), options.CopilotHome);

app.UseMiddleware<TokenMiddleware>();
app.MapAuditorEndpoints();

app.Run();

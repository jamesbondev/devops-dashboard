using Dashboard.Core.Scanners;
using Dashboard.Web;
using Dashboard.Web.Ado;
using Dashboard.Web.Ado.Mock;
using Dashboard.Web.Audit;
using Dashboard.Web.Components;
using Dashboard.Web.Data;
using Dashboard.Web.Services;
using Dashboard.Web.Sync;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

services.AddRazorComponents().AddInteractiveServerComponents();

// Config + core singletons
services.AddSingleton<ConfigService>();
services.AddSingleton<IClock, SystemClock>();
services.AddSingleton<AdoApiStats>();
services.AddSingleton<IdentityCache>();
services.AddSingleton<SyncSignals>();
services.AddSingleton<MockWorld>();

// Persistence
var dataDir = DatabaseInitializer.DataDirectory(builder.Configuration, builder.Environment);
Directory.CreateDirectory(dataDir);
services.AddDbContext<DashboardDbContext>(o => o.UseSqlite(DatabaseInitializer.ConnectionString(dataDir)));

// Azure DevOps (mock or live; switching mode requires a restart)
services.AddTransient<ThrottlingHandler>();
services.AddHttpClient<LiveAdoClient>().AddHttpMessageHandler<ThrottlingHandler>();
services.AddSingleton<MockAdoClient>();
services.AddSingleton<IAdoClient>(sp =>
{
    var cfg = sp.GetRequiredService<ConfigService>().Current;
    return cfg.AzureDevOps.IsMock ? sp.GetRequiredService<MockAdoClient>() : sp.GetRequiredService<LiveAdoClient>();
});
services.AddScoped<SyncService>();
services.AddHostedService<SyncScheduler>();

// Read models
services.AddSingleton<HealthService>();
services.AddSingleton<NowService>();
services.AddSingleton<StatusService>();
services.AddSingleton<RepoDetailService>();
services.AddSingleton<RepoAdminService>();
services.AddSingleton<UserStateService>();
services.AddSingleton<FindingsService>();
services.AddSingleton<InsightsService>();
services.AddSingleton<WeeklyService>();

// Audit pipeline
services.AddHttpClient<AuditorClient>();
services.AddHttpClient("nuget");
services.AddSingleton<IPackageMetadataProvider>(sp =>
{
    if (sp.GetRequiredService<ConfigService>().Current.AzureDevOps.IsMock && builder.Configuration["PACKAGE_LOOKUPS"] != "true")
        return OfflinePackageMetadataProvider.Instance;
    var f = sp.GetRequiredService<IHttpClientFactory>();
    return new CompositePackageMetadataProvider(new NuGetPackageMetadataProvider(f.CreateClient("nuget")), new TerraformRegistryMetadataProvider(f.CreateClient("nuget")));
});
services.AddSingleton(sp => new RepoScanner(sp.GetRequiredService<IPackageMetadataProvider>()));
services.AddSingleton<ICloneManager>(sp => sp.GetRequiredService<ConfigService>().Current.AzureDevOps.IsMock
    ? ActivatorUtilities.CreateInstance<MockCloneManager>(sp)
    : ActivatorUtilities.CreateInstance<GitCloneManager>(sp));
services.AddSingleton<AuditOrchestrator>();
services.AddHostedService(sp => sp.GetRequiredService<AuditOrchestrator>());

var app = builder.Build();

await DatabaseInitializer.InitialiseAsync(app.Services, dataDir, app.Logger);
var cfgSvc = app.Services.GetRequiredService<ConfigService>();
app.Logger.LogInformation("DevOps Dashboard starting: mode={Mode} org={Org} project={Project} config={Path} data={Data}",
    cfgSvc.Current.AzureDevOps.Mode, cfgSvc.Current.AzureDevOps.Organization, cfgSvc.Current.AzureDevOps.Project, cfgSvc.Path, dataDir);

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapDashboardEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

namespace Dashboard.Web
{
    /// <summary>Marker type for WebApplicationFactory in tests (Program stays internal).</summary>
    public sealed class WebEntryPoint;
}

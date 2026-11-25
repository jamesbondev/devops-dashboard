namespace Dashboard.Core.Scanners;

/// <summary>Derives components and Azure services from the other facts.</summary>
internal static class InferenceScanner
{
    private static readonly (string Prefix, string Service)[] PackageServices =
    [
        ("Azure.Messaging.ServiceBus", "Service Bus"), ("Microsoft.Azure.ServiceBus", "Service Bus"),
        ("MassTransit.Azure.ServiceBus", "Service Bus"), ("Microsoft.Azure.Functions.Worker.Extensions.ServiceBus", "Service Bus"),
        ("Microsoft.Azure.WebJobs.Extensions.ServiceBus", "Service Bus"), ("NServiceBus.Transport.AzureServiceBus", "Service Bus"),
        ("Azure.Security.KeyVault.", "Key Vault"), ("Azure.Extensions.AspNetCore.Configuration.Secrets", "Key Vault"),
        ("Azure.Storage.", "Storage"), ("Microsoft.Azure.Storage.", "Storage"), ("Azure.Data.Tables", "Storage"),
        ("Microsoft.Data.SqlClient", "SQL Database"), ("System.Data.SqlClient", "SQL Database"),
        ("Microsoft.EntityFrameworkCore.SqlServer", "SQL Database"),
        ("Azure.Identity", "Azure Identity"),
        ("Microsoft.Extensions.Configuration.AzureAppConfiguration", "App Configuration"), ("Azure.Data.AppConfiguration", "App Configuration"),
        ("Microsoft.Azure.AppConfiguration.", "App Configuration"),
        ("Microsoft.Azure.Cosmos", "Cosmos DB"), ("Microsoft.EntityFrameworkCore.Cosmos", "Cosmos DB"),
        ("Azure.Messaging.EventHubs", "Event Hubs"), ("Azure.Messaging.EventGrid", "Event Grid"),
        ("StackExchange.Redis", "Redis Cache"), ("Microsoft.Extensions.Caching.StackExchangeRedis", "Redis Cache"),
        ("Microsoft.ApplicationInsights", "Application Insights"), ("Azure.Monitor.OpenTelemetry", "Application Insights"),
        ("Azure.Search.Documents", "AI Search"), ("Azure.AI.OpenAI", "Azure OpenAI"),
    ];

    private static readonly (string Prefix, string Service)[] TerraformServices =
    [
        ("azurerm_servicebus_", "Service Bus"), ("azurerm_key_vault", "Key Vault"), ("azurerm_storage_", "Storage"),
        ("azurerm_mssql_", "SQL Database"), ("azurerm_sql_", "SQL Database"), ("azurerm_cosmosdb_", "Cosmos DB"),
        ("azurerm_eventhub", "Event Hubs"), ("azurerm_eventgrid_", "Event Grid"), ("azurerm_redis_", "Redis Cache"),
        ("azurerm_application_insights", "Application Insights"), ("azurerm_app_configuration", "App Configuration"),
        ("azurerm_kubernetes_cluster", "AKS"), ("azurerm_container_registry", "Container Registry"),
        ("azurerm_user_assigned_identity", "Managed Identity"), ("azurerm_federated_identity_credential", "Managed Identity"),
        ("azurerm_log_analytics_", "Log Analytics"), ("azurerm_postgresql_", "PostgreSQL"),
        ("azurerm_linux_function_app", "Functions"), ("azurerm_windows_function_app", "Functions"), ("azurerm_function_app", "Functions"),
        ("azurerm_linux_web_app", "App Service"), ("azurerm_windows_web_app", "App Service"), ("azurerm_app_service", "App Service"),
        ("azurerm_service_plan", "App Service"), ("azurerm_api_management", "API Management"),
        ("azurerm_virtual_network", "Virtual Network"), ("azurerm_dns_zone", "DNS"), ("azurerm_private_dns_zone", "DNS"),
        ("azurerm_search_service", "AI Search"), ("azurerm_cognitive_account", "Azure AI Services"),
    ];

    public static void Infer(RepoFacts facts)
    {
        facts.Components = InferComponents(facts);
        facts.AzureServices = InferAzureServices(facts);
    }

    internal static string ImageShortName(string image)
    {
        var i = image;
        var at = i.IndexOf('@');
        if (at >= 0) i = i[..at];
        var lastSlash = i.LastIndexOf('/');
        var colon = i.IndexOf(':', lastSlash + 1);
        if (colon >= 0) i = i[..colon];
        return lastSlash >= 0 ? i[(lastSlash + 1)..] : i;
    }

    private static List<ComponentHint> InferComponents(RepoFacts facts)
    {
        var result = new List<ComponentHint>();
        void Add(ComponentHint c)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) return;
            if (result.Any(x => x.Kind == c.Kind && x.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase))) return;
            result.Add(c);
        }

        foreach (var s in facts.Deploy.Skaffold)
            foreach (var a in s.Artifacts)
                Add(new ComponentHint { Name = ImageShortName(a.Image), Kind = "AksService", Source = "skaffold", Image = a.Image, Path = s.Path });

        foreach (var p in facts.Dotnet.Projects.Where(p => p.Kind == "Database"))
            Add(new ComponentHint { Name = p.Name, Kind = "Database", Source = "sqlproj", Path = p.Path });

        if (!facts.Deploy.Skaffold.Any(s => s.Artifacts.Count > 0))
        {
            foreach (var p in facts.Dotnet.Projects.Where(p => p.Kind is "Web" or "Worker" or "Functions"))
                Add(new ComponentHint { Name = p.Name, Kind = p.Kind == "Functions" ? "Other" : "AksService", Source = "csproj", Path = p.Path });
        }
        return result;
    }

    private static List<string> InferAzureServices(RepoFacts facts)
    {
        var services = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var id in facts.Dotnet.Projects.SelectMany(p => p.PackageReferences).Select(p => p.Id))
            foreach (var (prefix, service) in PackageServices)
                if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) services.Add(service);
        foreach (var r in facts.Terraform.ResourceTypes)
            foreach (var (prefix, service) in TerraformServices)
                if (r.StartsWith(prefix, StringComparison.Ordinal)) services.Add(service);
        if (facts.Data.SqlProjects.Count > 0) services.Add("SQL Database");
        return services.ToList();
    }
}

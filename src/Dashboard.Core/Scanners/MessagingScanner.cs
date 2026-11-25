using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dashboard.Core.Scanners;

internal static partial class MessagingScanner
{
    // String literals passed to well-known Service Bus / MassTransit / Functions APIs. Best effort.
    [GeneratedRegex(@"\b(?:CreateProcessor|CreateSessionProcessor|CreateSender|CreateReceiver|CreateSessionReceiver|AcceptNextSessionAsync|ReceiveEndpoint|SubscriptionEndpoint|ServiceBusTrigger|Topic|Queue)\s*(?:<[^>]*>)?\s*\(\s*""([^""]{1,200})""(?:\s*,\s*""([^""]{1,200})"")?")]
    private static partial Regex QueueLiteral();

    [GeneratedRegex(@"(?i)dead[-_ ]?letter|max_?delivery_?count|\$DeadLetterQueue|error[-_]?queue|UseMessageRetry|UseDelayedRedelivery|\.Retry\(|ConfigureDeadLetter")]
    private static partial Regex DeadLetterPattern();

    [GeneratedRegex(@"(?i)(queue|topic|subscription)(name|path)?s?$")]
    private static partial Regex QueueKey();

    [GeneratedRegex(@"\bIConsumer<|\bIHandleMessages<|\bServiceBusProcessor\b|\bProcessMessageAsync\b|\[ServiceBusTrigger\b|\bServiceBusReceiver\b|\bIJobConsumer<")]
    private static partial Regex ConsumerPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-/~]{0,259}$")]
    private static partial Regex PlausibleEntityName();

    public static void Scan(ScanContext ctx, RepoFacts facts)
    {
        var m = facts.Messaging;
        var ids = facts.Dotnet.Projects.SelectMany(p => p.PackageReferences).Select(p => p.Id).ToList();
        m.UsesServiceBus = ids.Any(id => id.Equals("Azure.Messaging.ServiceBus", StringComparison.OrdinalIgnoreCase)
            || id.Equals("Microsoft.Azure.ServiceBus", StringComparison.OrdinalIgnoreCase)
            || id.Equals("WindowsAzure.ServiceBus", StringComparison.OrdinalIgnoreCase)
            || id.Contains("ServiceBus", StringComparison.OrdinalIgnoreCase) && (id.StartsWith("Microsoft.Azure.Functions", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("Microsoft.Azure.WebJobs", StringComparison.OrdinalIgnoreCase) || id.StartsWith("MassTransit", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("NServiceBus.Transport.AzureServiceBus", StringComparison.OrdinalIgnoreCase)));
        m.UsesMassTransit = ids.Any(id => id.StartsWith("MassTransit", StringComparison.OrdinalIgnoreCase));
        m.UsesNServiceBus = ids.Any(id => id.StartsWith("NServiceBus", StringComparison.OrdinalIgnoreCase));
        var usesMessaging = m.UsesServiceBus || m.UsesMassTransit || m.UsesNServiceBus;

        var names = new SortedSet<string>(StringComparer.Ordinal);
        var consumers = new SortedSet<string>(StringComparer.Ordinal);
        var deadLetter = false;

        foreach (var f in ctx.WithExtension(".cs"))
        {
            var text = ctx.ReadText(f);
            if (text is null) continue;
            if (usesMessaging)
            {
                foreach (Match match in QueueLiteral().Matches(text))
                {
                    AddName(names, match.Groups[1].Value);
                    if (match.Groups[2].Success) AddName(names, match.Groups[2].Value);
                }
                if (ConsumerPattern().IsMatch(text)) consumers.Add(f.RelativePath);
            }
            if (!deadLetter && DeadLetterPattern().IsMatch(text)) deadLetter = true;
        }

        foreach (var f in ctx.Files.Where(f => f.Extension == ".json" && f.Name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)))
        {
            var text = ctx.ReadText(f);
            if (text is null) continue;
            if (!deadLetter && DeadLetterPattern().IsMatch(text)) deadLetter = true;
            try
            {
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                CollectJsonNames(doc.RootElement, null, names, 0);
            }
            catch (JsonException ex)
            {
                ctx.Warn($"Malformed JSON in '{f.RelativePath}': {ex.Message}");
            }
        }

        // Infra-side dead-letter config (terraform max_delivery_count, dead_lettering_on_message_expiration).
        if (!deadLetter)
            deadLetter = ctx.WithExtension(".tf", ".bicep").Any(f => ctx.ReadText(f) is { } t && DeadLetterPattern().IsMatch(t));

        m.QueueOrTopicNames = names.ToList();
        m.ConsumerFiles = consumers.ToList();
        m.DeadLetterHandlingFound = deadLetter;
    }

    private static void CollectJsonNames(JsonElement el, string? key, SortedSet<string> names, int depth)
    {
        if (depth > 32) return;
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject()) CollectJsonNames(p.Value, p.Name, names, depth + 1);
                break;
            case JsonValueKind.Array:
                if (key is not null && QueueKey().IsMatch(key))
                    foreach (var item in el.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String) AddName(names, item.GetString());
                foreach (var item in el.EnumerateArray())
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array) CollectJsonNames(item, key, names, depth + 1);
                break;
            case JsonValueKind.String:
                if (key is not null && QueueKey().IsMatch(key)) AddName(names, el.GetString());
                break;
        }
    }

    private static void AddName(SortedSet<string> names, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var v = value.Trim();
        // Skip connection strings, binding expressions (%Setting%), interpolations and URLs.
        if (v.Contains('=') || v.Contains(';') || v.Contains('%') || v.Contains('{') || v.Contains("://", StringComparison.Ordinal)) return;
        if (!PlausibleEntityName().IsMatch(v)) return;
        names.Add(v);
    }
}

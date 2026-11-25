using System.Reflection;
using Microsoft.Copilot;

namespace Dashboard.Auditor.Engines;

public static class SdkInfo
{
    /// <summary>Microsoft.Copilot.SDK informational version (without the source-link hash).</summary>
    public static string Version { get; } = Resolve();

    private static string Resolve()
    {
        var asm = typeof(CopilotClient).Assembly;
        var v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString()
                ?? "unknown";
        var plus = v.IndexOf('+', StringComparison.Ordinal);
        return "Microsoft.Copilot.SDK " + (plus > 0 ? v[..plus] : v);
    }
}

using System.Text;
using Microsoft.Copilot;

namespace Dashboard.Auditor.Engines;

/// <summary>Concise text transcript of a session: prompts, assistant messages, tool calls and errors.</summary>
public static class TranscriptBuilder
{
    private const int MaxUser = 600;
    private const int MaxAssistant = 4000;
    private const int MaxArgs = 300;

    public static void Append(StringBuilder sb, IEnumerable<SessionEvent> events)
    {
        foreach (var evt in events)
        {
            switch (evt)
            {
                case UserMessageEvent u:
                    sb.AppendLine($"[user] {Clip(u.Data.Content, MaxUser)}");
                    break;
                case AssistantMessageEvent a:
                    if (!string.IsNullOrWhiteSpace(a.Data.Content))
                    {
                        sb.AppendLine($"[assistant{(a.Data.Model is { } m ? " " + m : "")}] {Clip(a.Data.Content, MaxAssistant)}");
                    }
                    break;
                case ToolExecutionStartEvent s:
                    sb.AppendLine($"[tool] {s.Data.ToolName} {Clip(s.Data.Arguments?.GetRawText(), MaxArgs)}");
                    break;
                case ToolExecutionCompleteEvent c:
                    if (!c.Data.Success)
                    {
                        sb.AppendLine($"[tool-failed] {c.Data.ToolCallId}: {Clip(c.Data.Error?.Message, MaxArgs)}");
                    }
                    else
                    {
                        sb.AppendLine($"[tool-ok] {c.Data.ToolCallId} ({c.Data.Result?.Content?.Length ?? 0} chars)");
                    }
                    break;
                case SessionErrorEvent e:
                    sb.AppendLine($"[error] {e.Data.ErrorType} {e.Data.Message}");
                    break;
            }
        }
    }

    private static string Clip(string? s, int max)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }
        var oneLine = s.Length > max ? s[..max] + $"…(+{s.Length - max})" : s;
        return oneLine.Replace("\r", "", StringComparison.Ordinal);
    }
}

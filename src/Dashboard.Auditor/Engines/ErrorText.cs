using System.Text.RegularExpressions;

namespace Dashboard.Auditor.Engines;

public static partial class ErrorText
{
    private const int MaxLength = 500;

    // RemoteRpcException messages embed the whole HTTP response (incl. headers) as JSON.
    [GeneratedRegex("\"status\":(\\d+).*?\"body\":\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Singleline)]
    private static partial Regex HttpFailure();

    /// <summary>A short, single-line error for API responses and job state.</summary>
    public static string Summarise(Exception ex)
    {
        var message = ex.Message;
        var m = HttpFailure().Match(message);
        if (m.Success)
        {
            var head = message.Split(", {", 2)[0];
            string body;
            try
            {
                body = Regex.Unescape(m.Groups[2].Value).Trim();
            }
            catch (ArgumentException)
            {
                body = m.Groups[2].Value;
            }
            message = $"{head}: HTTP {m.Groups[1].Value} {body}";
        }
        message = message.ReplaceLineEndings(" ");
        return message.Length > MaxLength ? message[..MaxLength] + "…" : message;
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Dashboard.Auditor.Security;

/// <summary>Requires <c>X-Auditor-Token</c> on everything except <c>GET /health</c>.</summary>
public sealed class TokenMiddleware(RequestDelegate next, AuditorOptions options)
{
    public const string HeaderName = "X-Auditor-Token";

    private readonly byte[] _expected = Encoding.UTF8.GetBytes(options.Token);

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var supplied = context.Request.Headers[HeaderName].ToString();
        if (supplied.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), _expected))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = $"missing or invalid {HeaderName} header" });
            return;
        }

        await next(context);
    }
}

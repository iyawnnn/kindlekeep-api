using System.Text;

namespace KindleKeep.Api.Infrastructure.Security;

// Turns "which headers are missing" into "paste this into your host's config" — detection is a
// best-effort fingerprint off response headers already captured by WatcherEngine, not a guarantee.
public static class BlueprintGenerator
{
    private static readonly Dictionary<string, string> HeaderValues = new()
    {
        ["Content-Security-Policy"] = "default-src 'self'",
        ["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains; preload",
        ["X-Frame-Options"] = "DENY",
        ["X-Content-Type-Options"] = "nosniff"
    };

    public static string DetectPlatform(Dictionary<string, string> headers)
    {
        string? server = headers.TryGetValue("Server", out var s) ? s : null;

        if (headers.ContainsKey("CF-RAY") || (server?.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) ?? false))
            return "Cloudflare";
        if (headers.ContainsKey("x-vercel-id") || (server?.Contains("Vercel", StringComparison.OrdinalIgnoreCase) ?? false))
            return "Vercel";
        if (headers.ContainsKey("x-nf-request-id") || (server?.Contains("Netlify", StringComparison.OrdinalIgnoreCase) ?? false))
            return "Netlify";
        if (headers.TryGetValue("X-Powered-By", out var poweredBy) && poweredBy.Contains("Express", StringComparison.OrdinalIgnoreCase))
            return "Express";
        if (server?.Contains("nginx", StringComparison.OrdinalIgnoreCase) ?? false)
            return "Nginx";
        if (server?.Contains("Apache", StringComparison.OrdinalIgnoreCase) ?? false)
            return "Apache";

        return "Generic";
    }

    public static string GenerateSnippet(string platform, IReadOnlyList<string> missingHeaderKeys)
    {
        if (missingHeaderKeys.Count == 0) return string.Empty;

        return platform switch
        {
            "Vercel" => VercelJson(missingHeaderKeys),
            "Netlify" or "Cloudflare" => HeadersFile(missingHeaderKeys),
            "Nginx" or "Generic" => NginxConf(missingHeaderKeys),
            "Apache" => Htaccess(missingHeaderKeys),
            "Express" => ExpressMiddleware(missingHeaderKeys),
            _ => NginxConf(missingHeaderKeys)
        };
    }

    private static string VercelJson(IReadOnlyList<string> keys)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"headers\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"source\": \"/(.*)\",");
        sb.AppendLine("      \"headers\": [");
        for (int i = 0; i < keys.Count; i++)
        {
            var comma = i < keys.Count - 1 ? "," : "";
            sb.AppendLine($"        {{ \"key\": \"{keys[i]}\", \"value\": \"{HeaderValues[keys[i]]}\" }}{comma}");
        }
        sb.AppendLine("      ]");
        sb.AppendLine("    }");
        sb.AppendLine("  ]");
        sb.Append('}');
        return sb.ToString();
    }

    // Netlify and Cloudflare Pages both read a plain-text `_headers` file in this format.
    private static string HeadersFile(IReadOnlyList<string> keys)
    {
        var sb = new StringBuilder();
        sb.AppendLine("/*");
        foreach (var key in keys)
        {
            sb.AppendLine($"  {key}: {HeaderValues[key]}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string NginxConf(IReadOnlyList<string> keys)
    {
        var sb = new StringBuilder();
        foreach (var key in keys)
        {
            sb.AppendLine($"add_header {key} \"{HeaderValues[key]}\" always;");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Htaccess(IReadOnlyList<string> keys)
    {
        var sb = new StringBuilder();
        foreach (var key in keys)
        {
            sb.AppendLine($"Header always set {key} \"{HeaderValues[key]}\"");
        }
        return sb.ToString().TrimEnd();
    }

    private static string ExpressMiddleware(IReadOnlyList<string> keys)
    {
        var sb = new StringBuilder();
        sb.AppendLine("app.use((req, res, next) => {");
        foreach (var key in keys)
        {
            sb.AppendLine($"  res.setHeader('{key}', '{HeaderValues[key]}');");
        }
        sb.AppendLine("  next();");
        sb.Append("});");
        return sb.ToString();
    }
}

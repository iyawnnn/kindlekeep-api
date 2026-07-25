namespace KindleKeep.Api.Infrastructure.Badges;

// Renders a shields.io-style two-segment SVG status badge for embedding in READMEs.
public static class BadgeGenerator
{
    // ponytail: char-count * average glyph width, not real font metrics - fine for the fixed short
    // ASCII labels/messages this emits; swap for real text measurement if labels ever get longer/variable.
    private const double CharWidth = 6.5;
    private const double HorizontalPadding = 10;

    public static (string Message, string Color) DescribeStatus(int uptimeStatus, bool isActive)
    {
        if (!isActive) return ("paused", "#9f9f9f");

        return uptimeStatus switch
        {
            0 => ("operational", "#4c1"),    // Healthy
            1 => ("down", "#e05d44"),        // Down
            2 => ("degraded", "#dfb317"),    // Degraded
            3 => ("quarantined", "#9f9f9f"), // Quarantined
            _ => ("unknown", "#9f9f9f")
        };
    }

    public static string GenerateSvg(string label, string message, string color)
    {
        var labelWidth = TextWidth(label);
        var messageWidth = TextWidth(message);
        var totalWidth = labelWidth + messageWidth;

        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{totalWidth}" height="20" role="img" aria-label="{label}: {message}">
              <linearGradient id="s" x2="0" y2="100%">
                <stop offset="0" stop-color="#bbb" stop-opacity=".1"/>
                <stop offset="1" stop-opacity=".1"/>
              </linearGradient>
              <clipPath id="r">
                <rect width="{totalWidth}" height="20" rx="3" fill="#fff"/>
              </clipPath>
              <g clip-path="url(#r)">
                <rect width="{labelWidth}" height="20" fill="#555"/>
                <rect x="{labelWidth}" width="{messageWidth}" height="20" fill="{color}"/>
                <rect width="{totalWidth}" height="20" fill="url(#s)"/>
              </g>
              <g fill="#fff" text-anchor="middle" font-family="Verdana,Geneva,DejaVu Sans,sans-serif" font-size="11">
                <text x="{labelWidth / 2}" y="14">{label}</text>
                <text x="{labelWidth + messageWidth / 2}" y="14">{message}</text>
              </g>
            </svg>
            """;
    }

    private static double TextWidth(string text) => Math.Round(text.Length * CharWidth + HorizontalPadding * 2, 1);
}

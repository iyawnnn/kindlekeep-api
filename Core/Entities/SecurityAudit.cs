namespace KindleKeep.Api.Core.Entities;

public record SecurityAudit
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid MonitorId { get; init; }
    public DateTime? SslExpiryAt { get; init; }
    public string? SslIssuer { get; init; }
    
    // Core HTTP Defense Headers
    public bool HasCsp { get; init; }
    public bool HasHsts { get; init; }
    public bool HasXfo { get; init; }
    public bool HasNosniff { get; init; }
    
    public string? TlsVersion { get; init; }
    
    // JSONB payload for deep inspection later
    public string? RawHeaders { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    // Cached Gemini explanation for this exact audit row - set once, reused on every
    // subsequent /copilot call for the same audit so re-opening the modal never re-spends a token.
    public string? CopilotExplanation { get; set; }
    public DateTime? CopilotGeneratedAt { get; set; }

    // Navigation property
    public MonitorTarget? Monitor { get; init; }
}
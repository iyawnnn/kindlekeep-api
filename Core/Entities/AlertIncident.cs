namespace KindleKeep.Api.Core.Entities;

public record AlertIncident
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid MonitorId { get; init; }
    public required string IncidentHash { get; init; }
    public required string IncidentType { get; init; }
    public bool IsResolved { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime? EscalatedAt { get; set; }

    public MonitorTarget? Monitor { get; init; }
}
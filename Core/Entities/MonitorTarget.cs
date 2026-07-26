using KindleKeep.Api.Core.Enums;

namespace KindleKeep.Api.Core.Entities;

public record MonitorTarget
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid UserId { get; init; }
    public required string Url { get; init; }
    public required string FriendlyName { get; init; }
    
    public int IntervalMinutes { get; init; } = 10;
    public int RequestTimeout { get; init; } = 30;
    public MonitorType MonitorType { get; init; } = MonitorType.Http;
    
    // JSONB payload for things like Authorization headers
    public string? RequestHeaders { get; set; } 
    
    // Mutable properties updated by the Watcher Engine
    public string? LastAuditHash { get; set; }
    public bool IsActive { get; set; } = true;
    public UptimeStatus CurrentUptimeStatus { get; set; } = UptimeStatus.Healthy;
    public char CurrentSecurityGrade { get; set; } = 'U'; // 'U' stands for Untested
    public int FailureCount { get; set; } = 0;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastCheckedAt { get; set; }

    // Public status page opt-in
    public bool IsPublic { get; set; } = false;
    public string? PublicSlug { get; set; }

    // Navigation property
    public User? User { get; init; }
}
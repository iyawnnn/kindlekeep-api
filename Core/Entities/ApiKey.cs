namespace KindleKeep.Api.Core.Entities;

public record ApiKey
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid UserId { get; init; }
    public required string KeyHash { get; init; }
    public required string Label { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }

    // Navigation property
    public User? User { get; init; }
}

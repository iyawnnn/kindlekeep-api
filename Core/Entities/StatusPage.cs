namespace KindleKeep.Api.Core.Entities;

public record StatusPage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid UserId { get; init; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public bool IsPublished { get; set; } = false;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public User? User { get; init; }
    public List<StatusPageService> Services { get; init; } = [];
}

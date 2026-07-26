namespace KindleKeep.Api.Core.Entities;

public record StatusPageService
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid StatusPageId { get; init; }
    public required Guid MonitorId { get; init; }
    public required string DisplayName { get; set; }
    public string? SectionName { get; set; }
    public int SortOrder { get; set; } = 0;

    public StatusPage? StatusPage { get; init; }
    public MonitorTarget? Monitor { get; init; }
}

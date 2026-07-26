namespace KindleKeep.Api.Core.Entities;

public record JourneyStep
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid MonitorId { get; init; }
    public required int StepOrder { get; init; }
    public required string Method { get; init; }
    public required string UrlOrPath { get; init; }
    public string? Headers { get; init; }
    public string? Body { get; init; }
    public string? CaptureAs { get; init; }
    public string? CaptureJsonPath { get; init; }
    public string? AssertJsonPath { get; init; }
    public string? AssertEquals { get; init; }
    public int? ExpectedStatusCode { get; init; }

    public MonitorTarget? Monitor { get; init; }
}

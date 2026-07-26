using System;
using System.Text.Json.Serialization;

namespace KindleKeep.Api.Core.DTOs;

public record IncidentResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("monitorId")] Guid MonitorId,
    [property: JsonPropertyName("friendlyName")] string FriendlyName,
    [property: JsonPropertyName("incidentHash")] string IncidentHash,
    [property: JsonPropertyName("incidentType")] string IncidentType,
    [property: JsonPropertyName("isResolved")] bool IsResolved,
    [property: JsonPropertyName("startTime")] DateTime StartTime,
    [property: JsonPropertyName("resolvedAt")] DateTime? ResolvedAt,
    [property: JsonPropertyName("occurrenceCount")] int OccurrenceCount,
    [property: JsonPropertyName("acknowledgedAt")] DateTime? AcknowledgedAt,
    [property: JsonPropertyName("escalatedAt")] DateTime? EscalatedAt,
    [property: JsonPropertyName("mttrMinutes")] double? MttrMinutes
);

public record AcknowledgeResponse(
    [property: JsonPropertyName("acknowledgedAt")] DateTime AcknowledgedAt
);
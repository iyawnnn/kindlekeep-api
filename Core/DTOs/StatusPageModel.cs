using System.Collections.Generic;
using System.Text.Json.Serialization;
using KindleKeep.Api.Core.Enums;

namespace KindleKeep.Api.Core.DTOs;

public record CreateStatusPageRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("slug")] string Slug
);

public record UpdateStatusPageRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("isPublished")] bool IsPublished
);

public record StatusPageResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("isPublished")] bool IsPublished,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt
);

public record StatusPageSummaryResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("isPublished")] bool IsPublished,
    [property: JsonPropertyName("serviceCount")] int ServiceCount
);

public record AttachServiceRequest(
    [property: JsonPropertyName("monitorId")] Guid MonitorId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("sectionName")] string? SectionName
);

public record UpdateServiceRequest(
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("sectionName")] string? SectionName
);

public record StatusPageServiceResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("monitorId")] Guid MonitorId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("sectionName")] string? SectionName,
    [property: JsonPropertyName("sortOrder")] int SortOrder,
    [property: JsonPropertyName("currentUptimeStatus")] UptimeStatus CurrentUptimeStatus
);

public record ServiceOrderEntry(
    [property: JsonPropertyName("serviceId")] Guid ServiceId,
    [property: JsonPropertyName("sectionName")] string? SectionName,
    [property: JsonPropertyName("sortOrder")] int SortOrder
);

public record ReorderServicesRequest(
    [property: JsonPropertyName("services")] List<ServiceOrderEntry> Services
);

public record StatusPageDetailResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("isPublished")] bool IsPublished,
    [property: JsonPropertyName("services")] List<StatusPageServiceResponse> Services
);

public record PublicServiceResponse(
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("sectionName")] string? SectionName,
    [property: JsonPropertyName("sortOrder")] int SortOrder,
    [property: JsonPropertyName("currentUptimeStatus")] UptimeStatus CurrentUptimeStatus,
    [property: JsonPropertyName("recent24h")] List<UptimeLogResponse> Recent24h
);

public record PublicIncidentResponse(
    [property: JsonPropertyName("monitorDisplayName")] string MonitorDisplayName,
    [property: JsonPropertyName("incidentType")] string IncidentType,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("resolvedAt")] DateTime? ResolvedAt,
    [property: JsonPropertyName("mttrMinutes")] int? MttrMinutes
);

public record PublicStatusPageResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("aggregateStatus")] int AggregateStatus,
    [property: JsonPropertyName("services")] List<PublicServiceResponse> Services,
    [property: JsonPropertyName("recentIncidents")] List<PublicIncidentResponse> RecentIncidents
);

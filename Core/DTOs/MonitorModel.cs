using System.Collections.Generic;
using System.Text.Json.Serialization;
using KindleKeep.Api.Core.Enums;

namespace KindleKeep.Api.Core.DTOs;

public record CreateMonitorRequest(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("friendlyName")] string FriendlyName
);

public record MonitorResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("friendlyName")] string FriendlyName,
    [property: JsonPropertyName("currentUptimeStatus")] UptimeStatus CurrentUptimeStatus,
    [property: JsonPropertyName("currentSecurityGrade")] char CurrentSecurityGrade,
    [property: JsonPropertyName("isActive")] bool IsActive
);

public record UpdateMonitorRequest(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("friendlyName")] string FriendlyName,
    [property: JsonPropertyName("intervalMinutes")] int IntervalMinutes,
    [property: JsonPropertyName("requestTimeout")] int RequestTimeout,
    [property: JsonPropertyName("requestHeaders")] Dictionary<string, string>? RequestHeaders
);

public record MonitorDetailResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("friendlyName")] string FriendlyName,
    [property: JsonPropertyName("intervalMinutes")] int IntervalMinutes,
    [property: JsonPropertyName("requestTimeout")] int RequestTimeout,
    [property: JsonPropertyName("requestHeaders")] Dictionary<string, string>? RequestHeaders,
    [property: JsonPropertyName("currentUptimeStatus")] UptimeStatus CurrentUptimeStatus,
    [property: JsonPropertyName("currentSecurityGrade")] char CurrentSecurityGrade,
    [property: JsonPropertyName("isActive")] bool IsActive,
    [property: JsonPropertyName("isPublic")] bool IsPublic,
    [property: JsonPropertyName("publicSlug")] string? PublicSlug
);

public record SetPublicStatusRequest(
    [property: JsonPropertyName("enabled")] bool Enabled
);

public record PublicStatusResponse(
    [property: JsonPropertyName("isPublic")] bool IsPublic,
    [property: JsonPropertyName("publicSlug")] string? PublicSlug
);

public record PublicMonitorResponse(
    [property: JsonPropertyName("friendlyName")] string FriendlyName,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("currentUptimeStatus")] UptimeStatus CurrentUptimeStatus,
    [property: JsonPropertyName("isActive")] bool IsActive,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt,
    [property: JsonPropertyName("history")] List<UptimeLogResponse> History
);
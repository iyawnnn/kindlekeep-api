using System.Text.Json.Serialization;
using KindleKeep.Api.Core.Entities;

namespace KindleKeep.Api.Core.DTOs;

public record UserProfileResponse(
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("avatarUrl")] string? AvatarUrl,
    [property: JsonPropertyName("authProvider")] string AuthProvider
);

public record UserDataExportResponse(
    [property: JsonPropertyName("profile")] User Profile,
    [property: JsonPropertyName("monitors")] List<MonitorTarget> Monitors,
    [property: JsonPropertyName("uptimeLogs")] List<UptimeLog> UptimeLogs,
    [property: JsonPropertyName("securityAudits")] List<SecurityAudit> SecurityAudits,
    [property: JsonPropertyName("incidents")] List<AlertIncident> Incidents
);
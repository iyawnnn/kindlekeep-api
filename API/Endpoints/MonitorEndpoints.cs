using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using KindleKeep.Api.Core.DTOs;
using KindleKeep.Api.Core.Enums;
using KindleKeep.Api.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;

namespace KindleKeep.Api.Core.DTOs
{
    public record UptimeLogResponse(
        [property: JsonPropertyName("timestamp")] DateTime Timestamp,
        [property: JsonPropertyName("status")] UptimeStatus Status,
        [property: JsonPropertyName("latencyMs")] int LatencyMs
    );
}

namespace KindleKeep.Api.API.Endpoints
{
    public static class MonitorEndpoints
    {
        public static IEndpointRouteBuilder MapMonitorEndpoints(this IEndpointRouteBuilder endpoints)
        {
            var group = endpoints.MapGroup("/api/monitors").RequireAuthorization();

            group.MapGet("/", async ([FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                    ?? context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
                {
                    return Results.Unauthorized();
                }

                var monitors = new List<MonitorResponse>();
                
                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT \"Id\", \"Url\", \"FriendlyName\", \"CurrentUptimeStatus\", \"CurrentSecurityGrade\", \"IsActive\" FROM \"MonitorTargets\" WHERE \"UserId\" = $1";
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    monitors.Add(new MonitorResponse(
                        reader.GetGuid(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        (UptimeStatus)reader.GetInt32(3),
                        reader.GetString(4)[0],
                        reader.GetBoolean(5)
                    ));
                }

                return Results.Ok(monitors);
            });

            group.MapPost("/", async (CreateMonitorRequest request, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
                {
                    return Results.Unauthorized();
                }

                if (!ValidateMonitorInput(request.Url, request.FriendlyName, out var validationError))
                {
                    return Results.BadRequest(validationError);
                }

                await using (var quotaConnection = await dataSource.OpenConnectionAsync())
                await using (var quotaCommand = quotaConnection.CreateCommand())
                {
                    quotaCommand.CommandText = @"
                        SELECT
                            (SELECT COUNT(*) FROM ""MonitorTargets"" WHERE ""UserId"" = $1) as CurrentMonitors,
                            ""MonitorLimit""
                        FROM ""Users""
                        WHERE ""Id"" = $1;";
                    quotaCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                    await using var quotaReader = await quotaCommand.ExecuteReaderAsync();
                    if (await quotaReader.ReadAsync() && quotaReader.GetInt32(0) >= quotaReader.GetInt32(1))
                    {
                        return Results.Problem(detail: "Monitor limit reached for your account.", statusCode: StatusCodes.Status409Conflict);
                    }
                }

                var monitorId = Guid.NewGuid();

                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                
                command.CommandText = @"
                    INSERT INTO ""MonitorTargets"" (""Id"", ""UserId"", ""Url"", ""FriendlyName"", ""IntervalMinutes"", ""RequestTimeout"", ""IsActive"", ""CurrentUptimeStatus"", ""CurrentSecurityGrade"", ""UpdatedAt"")
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                    RETURNING ""Id"";";

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = monitorId });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = request.Url });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = request.FriendlyName });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = 10 });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = 30 });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = true });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = 0 });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "U" });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = DateTime.UtcNow });

                await command.ExecuteScalarAsync();

                var response = new MonitorResponse(
                    monitorId,
                    request.Url,
                    request.FriendlyName,
                    (UptimeStatus)0,
                    'U',
                    true
                );

                return Results.Created($"/api/monitors/{monitorId}", response);
            });

            group.MapDelete("/{id:guid}", async (Guid id, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                    ?? context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
                {
                    return Results.Unauthorized();
                }

                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM \"MonitorTargets\" WHERE \"Id\" = $1 AND \"UserId\" = $2";
                
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                var rowsAffected = await command.ExecuteNonQueryAsync();

                if (rowsAffected == 0)
                {
                    return Results.NotFound();
                }

                return Results.NoContent();
            });

            group.MapGet("/{id:guid}/audit", async (Guid id, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                    ?? context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
                {
                    return Results.Unauthorized();
                }

                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                
                command.CommandText = @"
                    SELECT sa.""HasCsp"", sa.""HasHsts"", sa.""HasXfo"", sa.""HasNosniff"", sa.""SslIssuer"", sa.""SslExpiryAt"", sa.""RawHeaders"", sa.""TlsVersion""
                    FROM ""SecurityAudits"" sa
                    INNER JOIN ""MonitorTargets"" mt ON sa.""MonitorId"" = mt.""Id""
                    WHERE mt.""Id"" = $1 AND mt.""UserId"" = $2
                    ORDER BY sa.""CreatedAt"" DESC
                    LIMIT 1;";

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                await using var reader = await command.ExecuteReaderAsync();

                if (!await reader.ReadAsync())
                {
                    return Results.NotFound();
                }

                bool hasCsp = reader.GetBoolean(0);
                bool hasHsts = reader.GetBoolean(1);
                bool hasXfo = reader.GetBoolean(2);
                bool hasNosniff = reader.GetBoolean(3);
                string? rawHeaders = reader.IsDBNull(6) ? null : reader.GetString(6);

                string? detectedPlatform = null;
                string? remediationSnippet = null;

                var missingHeaders = new List<string>();
                if (!hasCsp) missingHeaders.Add("Content-Security-Policy");
                if (!hasHsts) missingHeaders.Add("Strict-Transport-Security");
                if (!hasXfo) missingHeaders.Add("X-Frame-Options");
                if (!hasNosniff) missingHeaders.Add("X-Content-Type-Options");

                if (missingHeaders.Count > 0 && rawHeaders is not null)
                {
                    var headersDict = JsonSerializer.Deserialize(rawHeaders, AppJsonSerializerContext.Default.DictionaryStringString)
                        ?? [];
                    detectedPlatform = BlueprintGenerator.DetectPlatform(headersDict);
                    remediationSnippet = BlueprintGenerator.GenerateSnippet(detectedPlatform, missingHeaders);
                }

                var response = new SecurityAuditResponse(
                    hasCsp,
                    hasHsts,
                    hasXfo,
                    hasNosniff,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                    rawHeaders,
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    detectedPlatform,
                    remediationSnippet
                );

                return Results.Ok(response);
            });

            group.MapPatch("/{id:guid}/toggle", async (Guid id, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                    ?? context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
                {
                    return Results.Unauthorized();
                }

                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                
                command.CommandText = @"
                    UPDATE ""MonitorTargets"" 
                    SET ""IsActive"" = NOT ""IsActive"", ""UpdatedAt"" = $1 
                    WHERE ""Id"" = $2 AND ""UserId"" = $3
                    RETURNING ""IsActive"";";

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = DateTime.UtcNow });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                var result = await command.ExecuteScalarAsync();

                if (result == null)
                {
                    return Results.NotFound();
                }

                return Results.NoContent();
            });

            group.MapGet("/{id:guid}/history", async (Guid id, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                    ?? context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
                {
                    return Results.Unauthorized();
                }

                var logs = new List<UptimeLogResponse>();

                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                
                command.CommandText = @"
                    SELECT ul.""Timestamp"", ul.""StatusCode"", ul.""LatencyMs""
                    FROM ""UptimeLogs"" ul
                    INNER JOIN ""MonitorTargets"" mt ON ul.""MonitorId"" = mt.""Id""
                    WHERE mt.""Id"" = $1 AND mt.""UserId"" = $2
                    ORDER BY ul.""Timestamp"" DESC
                    LIMIT 144;";

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                await using var reader = await command.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    var statusCode = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
                    logs.Add(new UptimeLogResponse(
                        reader.GetDateTime(0),
                        statusCode is >= 200 and < 300 ? UptimeStatus.Healthy : UptimeStatus.Down,
                        reader.GetInt32(2)
                    ));
                }

                logs.Reverse();
                return Results.Ok(logs);
            });

            group.MapPost("/{id:guid}/reset-circuit", async (Guid id, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                    ?? context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
                {
                    return Results.Unauthorized();
                }

                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                
                command.CommandText = @"
                    UPDATE ""MonitorTargets"" 
                    SET ""FailureCount"" = 0, ""CurrentUptimeStatus"" = 0, ""UpdatedAt"" = $1 
                    WHERE ""Id"" = $2 AND ""UserId"" = $3";

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = DateTime.UtcNow });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                var rowsAffected = await command.ExecuteNonQueryAsync();

                if (rowsAffected == 0)
                {
                    return Results.NotFound();
                }

                return Results.NoContent();
            });

            group.MapGet("/{id:guid}", async (Guid id, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
                {
                    return Results.Unauthorized();
                }

                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();

                command.CommandText = @"
                    SELECT ""Id"", ""Url"", ""FriendlyName"", ""IntervalMinutes"", ""RequestTimeout"", ""RequestHeaders"",
                           ""CurrentUptimeStatus"", ""CurrentSecurityGrade"", ""IsActive"", ""IsPublic"", ""PublicSlug""
                    FROM ""MonitorTargets""
                    WHERE ""Id"" = $1 AND ""UserId"" = $2;";

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                await using var reader = await command.ExecuteReaderAsync();

                if (!await reader.ReadAsync())
                {
                    return Results.NotFound();
                }

                var response = new MonitorDetailResponse(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : JsonSerializer.Deserialize(reader.GetString(5), AppJsonSerializerContext.Default.DictionaryStringString),
                    (UptimeStatus)reader.GetInt32(6),
                    reader.GetString(7)[0],
                    reader.GetBoolean(8),
                    reader.GetBoolean(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10)
                );

                return Results.Ok(response);
            });

            group.MapPatch("/{id:guid}", async (Guid id, UpdateMonitorRequest request, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
                {
                    return Results.Unauthorized();
                }

                if (!ValidateMonitorInput(request.Url, request.FriendlyName, out var validationError))
                {
                    return Results.BadRequest(validationError);
                }

                var intervalMinutes = Math.Max(1, request.IntervalMinutes);
                var requestTimeout = Math.Clamp(request.RequestTimeout, 1, 60);
                var requestHeadersJson = request.RequestHeaders is null
                    ? null
                    : JsonSerializer.Serialize(request.RequestHeaders, AppJsonSerializerContext.Default.DictionaryStringString);

                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();

                command.CommandText = @"
                    UPDATE ""MonitorTargets""
                    SET ""Url"" = $1, ""FriendlyName"" = $2, ""IntervalMinutes"" = $3, ""RequestTimeout"" = $4,
                        ""RequestHeaders"" = $5, ""UpdatedAt"" = $6
                    WHERE ""Id"" = $7 AND ""UserId"" = $8
                    RETURNING ""Id"", ""Url"", ""FriendlyName"", ""IntervalMinutes"", ""RequestTimeout"", ""RequestHeaders"",
                              ""CurrentUptimeStatus"", ""CurrentSecurityGrade"", ""IsActive"", ""IsPublic"", ""PublicSlug"";";

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = request.Url });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = request.FriendlyName });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = intervalMinutes });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = requestTimeout });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = (object?)requestHeadersJson ?? DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = DateTime.UtcNow });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                await using var reader = await command.ExecuteReaderAsync();

                if (!await reader.ReadAsync())
                {
                    return Results.NotFound();
                }

                var response = new MonitorDetailResponse(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : JsonSerializer.Deserialize(reader.GetString(5), AppJsonSerializerContext.Default.DictionaryStringString),
                    (UptimeStatus)reader.GetInt32(6),
                    reader.GetString(7)[0],
                    reader.GetBoolean(8),
                    reader.GetBoolean(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10)
                );

                return Results.Ok(response);
            });

            group.MapPatch("/{id:guid}/public-status", async (Guid id, SetPublicStatusRequest request, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
                {
                    return Results.Unauthorized();
                }

                // Generated once and kept stable across future toggles via COALESCE below,
                // so disabling a public page doesn't burn the shareable link.
                var newSlug = Guid.NewGuid().ToString("N");

                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();

                command.CommandText = @"
                    UPDATE ""MonitorTargets""
                    SET ""IsPublic"" = $1, ""PublicSlug"" = COALESCE(""PublicSlug"", $2)
                    WHERE ""Id"" = $3 AND ""UserId"" = $4
                    RETURNING ""IsPublic"", ""PublicSlug"";";

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = request.Enabled });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = newSlug });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                await using var reader = await command.ExecuteReaderAsync();

                if (!await reader.ReadAsync())
                {
                    return Results.NotFound();
                }

                var response = new PublicStatusResponse(
                    reader.GetBoolean(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1)
                );

                return Results.Ok(response);
            });

            return endpoints;
        }

        private static bool ValidateMonitorInput(string url, string friendlyName, out string? error)
        {
            if (string.IsNullOrWhiteSpace(friendlyName))
            {
                error = "Friendly Name cannot be empty.";
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uriResult) ||
                (uriResult.Scheme != Uri.UriSchemeHttp && uriResult.Scheme != Uri.UriSchemeHttps))
            {
                error = "Invalid URL format. Must be an absolute HTTP or HTTPS URI.";
                return false;
            }

            error = null;
            return true;
        }
    }
}
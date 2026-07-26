using System.Security.Claims;
using System.Text.Json.Serialization;
using KindleKeep.Api.Core.DTOs;
using KindleKeep.Api.Core.Entities;
using KindleKeep.Api.Core.Enums;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;

namespace KindleKeep.Api.Core.DTOs
{
    public record UpdateSettingsRequest(
        [property: JsonPropertyName("discordWebhookUrl")] string? DiscordWebhookUrl,
        [property: JsonPropertyName("enableEmailNotifications")] bool EnableEmailNotifications,
        [property: JsonPropertyName("slackWebhookUrl")] string? SlackWebhookUrl,
        [property: JsonPropertyName("digestEnabled")] bool DigestEnabled,
        [property: JsonPropertyName("escalationDelayMinutes")] int EscalationDelayMinutes
    );

    public record UserUsageResponse(
        [property: JsonPropertyName("currentMonitors")] int CurrentMonitors,
        [property: JsonPropertyName("monitorLimit")] int MonitorLimit
    );

    public record UserSettingsResponse(
        [property: JsonPropertyName("discordWebhookUrl")] string? DiscordWebhookUrl,
        [property: JsonPropertyName("enableEmailNotifications")] bool EnableEmailNotifications,
        [property: JsonPropertyName("slackWebhookUrl")] string? SlackWebhookUrl,
        [property: JsonPropertyName("digestEnabled")] bool DigestEnabled,
        [property: JsonPropertyName("escalationDelayMinutes")] int EscalationDelayMinutes
    );
}

namespace KindleKeep.Api.API.Endpoints
{
    public static class UserEndpoints
    {
        public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder endpoints)
        {
            var group = endpoints.MapGroup("/api/users").RequireAuthorization();

            group.MapGet("/profile", async ([FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
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
                    SELECT ""DisplayName"", ""Email"", ""AvatarUrl"", ""AuthProvider""
                    FROM ""Users""
                    WHERE ""Id"" = $1;";

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                await using var reader = await command.ExecuteReaderAsync();

                if (!await reader.ReadAsync())
                {
                    return Results.NotFound("User not found.");
                }

                var response = new UserProfileResponse(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    ((AuthProvider)reader.GetInt32(3)).ToString()
                );

                return Results.Ok(response);
            });

            group.MapGet("/export", async ([FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
                {
                    return Results.Unauthorized();
                }

                await using var connection = await dataSource.OpenConnectionAsync();

                User? profile = null;
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        SELECT ""Id"", ""ExternalId"", ""AuthProvider"", ""Email"", ""DisplayName"", ""AvatarUrl"", ""CreatedAt"",
                               ""DiscordWebhookUrl"", ""EnableEmailNotifications"", ""SlackWebhookUrl"", ""DigestEnabled""
                        FROM ""Users"" WHERE ""Id"" = $1;";
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                    await using var reader = await command.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        profile = new User
                        {
                            Id = reader.GetGuid(0),
                            ExternalId = reader.GetString(1),
                            AuthProvider = (AuthProvider)reader.GetInt32(2),
                            Email = reader.GetString(3),
                            DisplayName = reader.GetString(4),
                            AvatarUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
                            CreatedAt = reader.GetDateTime(6),
                            DiscordWebhookUrl = reader.IsDBNull(7) ? null : reader.GetString(7),
                            EnableEmailNotifications = reader.GetBoolean(8),
                            SlackWebhookUrl = reader.IsDBNull(9) ? null : reader.GetString(9),
                            DigestEnabled = reader.GetBoolean(10)
                        };
                    }
                }

                if (profile == null)
                {
                    return Results.NotFound("User not found.");
                }

                var monitors = new List<MonitorTarget>();
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        SELECT ""Id"", ""UserId"", ""Url"", ""FriendlyName"", ""IntervalMinutes"", ""RequestTimeout"", ""RequestHeaders"",
                               ""LastAuditHash"", ""IsActive"", ""CurrentUptimeStatus"", ""CurrentSecurityGrade"", ""FailureCount"",
                               ""UpdatedAt"", ""LastCheckedAt"", ""IsPublic"", ""PublicSlug""
                        FROM ""MonitorTargets"" WHERE ""UserId"" = $1;";
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        monitors.Add(new MonitorTarget
                        {
                            Id = reader.GetGuid(0),
                            UserId = reader.GetGuid(1),
                            Url = reader.GetString(2),
                            FriendlyName = reader.GetString(3),
                            IntervalMinutes = reader.GetInt32(4),
                            RequestTimeout = reader.GetInt32(5),
                            RequestHeaders = reader.IsDBNull(6) ? null : reader.GetString(6),
                            LastAuditHash = reader.IsDBNull(7) ? null : reader.GetString(7),
                            IsActive = reader.GetBoolean(8),
                            CurrentUptimeStatus = (UptimeStatus)reader.GetInt32(9),
                            CurrentSecurityGrade = reader.GetString(10)[0],
                            FailureCount = reader.GetInt32(11),
                            UpdatedAt = reader.GetDateTime(12),
                            LastCheckedAt = reader.IsDBNull(13) ? null : reader.GetDateTime(13),
                            IsPublic = reader.GetBoolean(14),
                            PublicSlug = reader.IsDBNull(15) ? null : reader.GetString(15)
                        });
                    }
                }

                var monitorIds = monitors.Select(m => m.Id).ToArray();

                var uptimeLogs = new List<UptimeLog>();
                var securityAudits = new List<SecurityAudit>();
                var incidents = new List<AlertIncident>();

                if (monitorIds.Length > 0)
                {
                    await using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                            SELECT ""Id"", ""MonitorId"", ""StatusCode"", ""LatencyMs"", ""IsColdStart"", ""ErrorMessage"", ""Timestamp""
                            FROM ""UptimeLogs"" WHERE ""MonitorId"" = ANY($1);";
                        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = monitorIds });

                        await using var reader = await command.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                        {
                            uptimeLogs.Add(new UptimeLog
                            {
                                Id = reader.GetInt64(0),
                                MonitorId = reader.GetGuid(1),
                                StatusCode = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                                LatencyMs = reader.GetInt32(3),
                                IsColdStart = reader.GetBoolean(4),
                                ErrorMessage = reader.IsDBNull(5) ? null : reader.GetString(5),
                                Timestamp = reader.GetDateTime(6)
                            });
                        }
                    }

                    await using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                            SELECT ""Id"", ""MonitorId"", ""SslExpiryAt"", ""SslIssuer"", ""HasCsp"", ""HasHsts"", ""HasXfo"", ""HasNosniff"",
                                   ""TlsVersion"", ""RawHeaders"", ""CreatedAt""
                            FROM ""SecurityAudits"" WHERE ""MonitorId"" = ANY($1);";
                        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = monitorIds });

                        await using var reader = await command.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                        {
                            securityAudits.Add(new SecurityAudit
                            {
                                Id = reader.GetGuid(0),
                                MonitorId = reader.GetGuid(1),
                                SslExpiryAt = reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                                SslIssuer = reader.IsDBNull(3) ? null : reader.GetString(3),
                                HasCsp = reader.GetBoolean(4),
                                HasHsts = reader.GetBoolean(5),
                                HasXfo = reader.GetBoolean(6),
                                HasNosniff = reader.GetBoolean(7),
                                TlsVersion = reader.IsDBNull(8) ? null : reader.GetString(8),
                                RawHeaders = reader.IsDBNull(9) ? null : reader.GetString(9),
                                CreatedAt = reader.GetDateTime(10)
                            });
                        }
                    }

                    await using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                            SELECT ""Id"", ""MonitorId"", ""IncidentHash"", ""IncidentType"", ""IsResolved"", ""CreatedAt"", ""ResolvedAt""
                            FROM ""AlertIncidents"" WHERE ""MonitorId"" = ANY($1);";
                        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = monitorIds });

                        await using var reader = await command.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                        {
                            incidents.Add(new AlertIncident
                            {
                                Id = reader.GetGuid(0),
                                MonitorId = reader.GetGuid(1),
                                IncidentHash = reader.GetString(2),
                                IncidentType = reader.GetString(3),
                                IsResolved = reader.GetBoolean(4),
                                CreatedAt = reader.GetDateTime(5),
                                ResolvedAt = reader.IsDBNull(6) ? null : reader.GetDateTime(6)
                            });
                        }
                    }
                }

                var response = new UserDataExportResponse(profile, monitors, uptimeLogs, securityAudits, incidents);

                return Results.Ok(response);
            });

            group.MapDelete("/me", async ([FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
                {
                    return Results.Unauthorized();
                }

                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();

                // Every owned table (MonitorTargets, and everything keyed off MonitorId beneath it)
                // is FK-cascaded to Users at the DB level - this single delete sweeps the whole graph.
                command.CommandText = @"DELETE FROM ""Users"" WHERE ""Id"" = $1;";
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                var rowsAffected = await command.ExecuteNonQueryAsync();

                if (rowsAffected == 0)
                {
                    return Results.NotFound("User not found.");
                }

                return Results.NoContent();
            });

            group.MapGet("/usage", async ([FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
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
                    SELECT 
                        (SELECT COUNT(*) FROM ""MonitorTargets"" WHERE ""UserId"" = $1) as CurrentMonitors,
                        ""MonitorLimit""
                    FROM ""Users"" 
                    WHERE ""Id"" = $1;";
                
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                await using var reader = await command.ExecuteReaderAsync();

                if (!await reader.ReadAsync())
                {
                    return Results.NotFound("User not found.");
                }

                var response = new UserUsageResponse(
                    reader.GetInt32(0),
                    reader.GetInt32(1)
                );

                return Results.Ok(response);
            });

            group.MapGet("/settings", async ([FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
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
                    SELECT ""DiscordWebhookUrl"", ""EnableEmailNotifications"", ""SlackWebhookUrl"", ""DigestEnabled"", ""EscalationDelayMinutes""
                    FROM ""Users""
                    WHERE ""Id"" = $1;";

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                await using var reader = await command.ExecuteReaderAsync();

                if (!await reader.ReadAsync())
                {
                    return Results.NotFound("User not found.");
                }

                var response = new UserSettingsResponse(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.GetBoolean(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetBoolean(3),
                    reader.GetInt32(4)
                );

                return Results.Ok(response);
            });

            group.MapPut("/settings", async (UpdateSettingsRequest request, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
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
                    UPDATE ""Users""
                    SET ""DiscordWebhookUrl"" = $1,
                        ""EnableEmailNotifications"" = $2,
                        ""SlackWebhookUrl"" = $3,
                        ""DigestEnabled"" = $4,
                        ""EscalationDelayMinutes"" = $5
                    WHERE ""Id"" = $6
                    RETURNING ""Id"";";

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = request.DiscordWebhookUrl ?? (object)DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = request.EnableEmailNotifications });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = request.SlackWebhookUrl ?? (object)DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = request.DigestEnabled });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = Math.Max(1, request.EscalationDelayMinutes) });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                var result = await command.ExecuteScalarAsync();

                if (result == null)
                {
                    return Results.NotFound("User not found.");
                }

                return Results.NoContent();
            });

            return endpoints;
        }
    }
}
using System;
using System.Collections.Generic;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;
using KindleKeep.Api.Core.DTOs;

namespace KindleKeep.Api.API.Endpoints
{
    public static class IncidentEndpoints
    {
        public static IEndpointRouteBuilder MapIncidentEndpoints(this IEndpointRouteBuilder endpoints)
        {
            var group = endpoints.MapGroup("/api/incidents").RequireAuthorization();

            group.MapGet("/", async ([FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                    ?? context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
                {
                    return Results.Unauthorized();
                }

                var incidents = new List<IncidentResponse>();

                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                
                command.CommandText = @"
                    SELECT
                        ai.""Id"",
                        ai.""MonitorId"",
                        mt.""FriendlyName"",
                        ai.""IncidentHash"",
                        ai.""IncidentType"",
                        ai.""IsResolved"",
                        ai.""CreatedAt"" as StartTime,
                        ai.""ResolvedAt"",
                        (SELECT COUNT(*) FROM ""UptimeLogs"" ul
                         WHERE ul.""MonitorId"" = ai.""MonitorId""
                           AND ul.""ErrorMessage"" IS NOT NULL
                           AND ul.""Timestamp"" >= ai.""CreatedAt""
                           AND (ai.""ResolvedAt"" IS NULL OR ul.""Timestamp"" <= ai.""ResolvedAt"")) as OccurrenceCount,
                        ai.""AcknowledgedAt"",
                        ai.""EscalatedAt"",
                        CASE WHEN ai.""ResolvedAt"" IS NOT NULL
                             THEN EXTRACT(EPOCH FROM (ai.""ResolvedAt"" - ai.""CreatedAt"")) / 60.0
                             ELSE NULL END as MttrMinutes
                    FROM ""AlertIncidents"" ai
                    INNER JOIN ""MonitorTargets"" mt ON ai.""MonitorId"" = mt.""Id""
                    WHERE mt.""UserId"" = $1
                    ORDER BY ai.""CreatedAt"" DESC;";

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                await using var reader = await command.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    incidents.Add(new IncidentResponse(
                        reader.GetGuid(0),
                        reader.GetGuid(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetBoolean(5),
                        reader.GetDateTime(6),
                        reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                        reader.GetInt32(8),
                        reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                        reader.IsDBNull(10) ? null : reader.GetDateTime(10),
                        reader.IsDBNull(11) ? null : reader.GetDouble(11)
                    ));
                }

                return Results.Ok(incidents);
            });

            group.MapPost("/{id:guid}/acknowledge", async (Guid id, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
                {
                    return Results.Unauthorized();
                }

                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();

                // COALESCE makes this idempotent - acknowledging an already-acknowledged incident
                // returns the original timestamp instead of bumping it or erroring.
                command.CommandText = @"
                    UPDATE ""AlertIncidents"" ai
                    SET ""AcknowledgedAt"" = COALESCE(ai.""AcknowledgedAt"", $1)
                    FROM ""MonitorTargets"" mt
                    WHERE ai.""MonitorId"" = mt.""Id""
                      AND ai.""Id"" = $2
                      AND mt.""UserId"" = $3
                    RETURNING ai.""AcknowledgedAt"";";

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = DateTime.UtcNow });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                await using var reader = await command.ExecuteReaderAsync();

                if (!await reader.ReadAsync())
                {
                    return Results.NotFound();
                }

                return Results.Ok(new AcknowledgeResponse(reader.GetDateTime(0)));
            });

            return endpoints;
        }
    }
}
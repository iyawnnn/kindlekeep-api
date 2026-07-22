using System;
using System.Collections.Generic;
using KindleKeep.Api.Core.DTOs;
using KindleKeep.Api.Core.Enums;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;

namespace KindleKeep.Api.API.Endpoints
{
    public static class PublicEndpoints
    {
        public static IEndpointRouteBuilder MapPublicEndpoints(this IEndpointRouteBuilder endpoints)
        {
            var group = endpoints.MapGroup("/api/public");

            group.MapGet("/monitors/{slug}", async (string slug, [FromServices] NpgsqlDataSource dataSource) =>
            {
                await using var connection = await dataSource.OpenConnectionAsync();

                Guid monitorId;
                string friendlyName;
                string url;
                UptimeStatus status;
                bool isActive;
                DateTime updatedAt;

                await using (var command = connection.CreateCommand())
                {
                    // Same 404 whether the slug doesn't exist or the monitor isn't public -
                    // don't leak which. Never selects CurrentSecurityGrade/RequestHeaders here;
                    // audit internals stay behind auth.
                    command.CommandText = @"
                        SELECT ""Id"", ""FriendlyName"", ""Url"", ""CurrentUptimeStatus"", ""IsActive"", ""UpdatedAt""
                        FROM ""MonitorTargets""
                        WHERE ""PublicSlug"" = $1 AND ""IsPublic"" = true;";
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = slug });

                    await using var reader = await command.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return Results.NotFound();
                    }

                    monitorId = reader.GetGuid(0);
                    friendlyName = reader.GetString(1);
                    url = reader.GetString(2);
                    status = (UptimeStatus)reader.GetInt32(3);
                    isActive = reader.GetBoolean(4);
                    updatedAt = reader.GetDateTime(5);
                }

                var history = new List<UptimeLogResponse>();

                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        SELECT ""Timestamp"", ""StatusCode"", ""LatencyMs""
                        FROM ""UptimeLogs""
                        WHERE ""MonitorId"" = $1
                        ORDER BY ""Timestamp"" DESC
                        LIMIT 144;";
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = monitorId });

                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var statusCode = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
                        history.Add(new UptimeLogResponse(
                            reader.GetDateTime(0),
                            statusCode is >= 200 and < 300 ? UptimeStatus.Healthy : UptimeStatus.Down,
                            reader.GetInt32(2)
                        ));
                    }
                }

                history.Reverse();

                var response = new PublicMonitorResponse(friendlyName, url, status, isActive, updatedAt, history);
                return Results.Ok(response);
            });

            return endpoints;
        }
    }
}

using System;
using System.Collections.Generic;
using KindleKeep.Api.Core.DTOs;
using KindleKeep.Api.Core.Enums;
using KindleKeep.Api.Infrastructure.Badges;
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

            group.MapGet("/monitors/{slug}/badge.svg", async (string slug, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();

                command.CommandText = @"
                    SELECT ""CurrentUptimeStatus"", ""IsActive""
                    FROM ""MonitorTargets""
                    WHERE ""PublicSlug"" = $1 AND ""IsPublic"" = true;";
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = slug });

                string message;
                string color;

                await using (var reader = await command.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        (message, color) = BadgeGenerator.DescribeStatus(reader.GetInt32(0), reader.GetBoolean(1));
                    }
                    else
                    {
                        // Same response whether the slug doesn't exist or isn't public - don't leak which.
                        (message, color) = ("unknown", "#9f9f9f");
                    }
                }

                // Best-effort - GitHub's camo image proxy applies its own caching on top of this regardless.
                context.Response.Headers.CacheControl = "no-cache, max-age=60";
                return Results.Content(BadgeGenerator.GenerateSvg("kindlekeep", message, color), "image/svg+xml");
            });

            group.MapGet("/status-pages/{slug}", async (string slug, [FromServices] NpgsqlDataSource dataSource) =>
            {
                await using var connection = await dataSource.OpenConnectionAsync();

                Guid pageId;
                string name;

                await using (var command = connection.CreateCommand())
                {
                    // Same 404 whether the slug doesn't exist or the page isn't published -
                    // don't leak which.
                    command.CommandText = @"
                        SELECT ""Id"", ""Name""
                        FROM ""StatusPages""
                        WHERE ""Slug"" = $1 AND ""IsPublished"" = true;";
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = slug });

                    await using var reader = await command.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return Results.NotFound();
                    }

                    pageId = reader.GetGuid(0);
                    name = reader.GetString(1);
                }

                var serviceRows = new List<(Guid MonitorId, string DisplayName, string? SectionName, int SortOrder, UptimeStatus Status)>();

                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        SELECT s.""MonitorId"", s.""DisplayName"", s.""SectionName"", s.""SortOrder"", mt.""CurrentUptimeStatus""
                        FROM ""StatusPageServices"" s
                        INNER JOIN ""MonitorTargets"" mt ON mt.""Id"" = s.""MonitorId""
                        WHERE s.""StatusPageId"" = $1
                        ORDER BY s.""SectionName"" NULLS FIRST, s.""SortOrder"";";
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = pageId });

                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        serviceRows.Add((
                            reader.GetGuid(0),
                            reader.GetString(1),
                            reader.IsDBNull(2) ? null : reader.GetString(2),
                            reader.GetInt32(3),
                            (UptimeStatus)reader.GetInt32(4)
                        ));
                    }
                }

                var monitorIds = serviceRows.ConvertAll(r => r.MonitorId);
                var historyByMonitor = new Dictionary<Guid, List<UptimeLogResponse>>();

                if (monitorIds.Count > 0)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = @"
                        SELECT ""MonitorId"", ""Timestamp"", ""StatusCode"", ""LatencyMs""
                        FROM ""UptimeLogs""
                        WHERE ""MonitorId"" = ANY($1)
                        ORDER BY ""Timestamp"" DESC;";
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = monitorIds.ToArray() });

                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var monitorId = reader.GetGuid(0);
                        if (!historyByMonitor.TryGetValue(monitorId, out var list))
                        {
                            list = [];
                            historyByMonitor[monitorId] = list;
                        }

                        if (list.Count >= 144) continue;

                        var statusCode = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
                        list.Add(new UptimeLogResponse(
                            reader.GetDateTime(1),
                            statusCode is >= 200 and < 300 ? UptimeStatus.Healthy : UptimeStatus.Down,
                            reader.GetInt32(3)
                        ));
                    }
                }

                var services = new List<PublicServiceResponse>();
                foreach (var row in serviceRows)
                {
                    var history = historyByMonitor.TryGetValue(row.MonitorId, out var list) ? list : [];
                    history.Reverse();
                    services.Add(new PublicServiceResponse(row.DisplayName, row.SectionName, row.SortOrder, row.Status, history));
                }

                var downCount = 0;
                foreach (var row in serviceRows)
                {
                    if (row.Status is UptimeStatus.Down or UptimeStatus.Quarantined) downCount++;
                }

                var aggregateStatus = services.Count == 0 || downCount == 0
                    ? 0 // Operational
                    : downCount >= services.Count || downCount > services.Count / 2
                        ? 2 // MajorOutage
                        : 1; // PartialOutage

                var incidents = new List<PublicIncidentResponse>();

                if (monitorIds.Count > 0)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = @"
                        SELECT mt.""FriendlyName"", ai.""IncidentType"", ai.""CreatedAt"", ai.""ResolvedAt""
                        FROM ""AlertIncidents"" ai
                        INNER JOIN ""MonitorTargets"" mt ON mt.""Id"" = ai.""MonitorId""
                        WHERE ai.""MonitorId"" = ANY($1)
                        ORDER BY ai.""CreatedAt"" DESC
                        LIMIT 20;";
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = monitorIds.ToArray() });

                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var createdAt = reader.GetDateTime(2);
                        DateTime? resolvedAt = reader.IsDBNull(3) ? null : reader.GetDateTime(3);
                        int? mttrMinutes = resolvedAt is null ? null : (int)(resolvedAt.Value - createdAt).TotalMinutes;

                        incidents.Add(new PublicIncidentResponse(
                            reader.GetString(0),
                            reader.GetString(1),
                            createdAt,
                            resolvedAt,
                            mttrMinutes
                        ));
                    }
                }

                var response = new PublicStatusPageResponse(name, aggregateStatus, services, incidents);
                return Results.Ok(response);
            });

            return endpoints;
        }
    }
}

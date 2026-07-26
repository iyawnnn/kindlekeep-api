using System;
using System.Collections.Generic;
using System.Security.Claims;
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
    public static class StatusPageEndpoints
    {
        public static IEndpointRouteBuilder MapStatusPageEndpoints(this IEndpointRouteBuilder endpoints)
        {
            var group = endpoints.MapGroup("/api/status-pages").RequireAuthorization();

            group.MapGet("/", async ([FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                if (!TryGetUserId(context, out var userId))
                {
                    return Results.Unauthorized();
                }

                var pages = new List<StatusPageSummaryResponse>();

                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT sp.""Id"", sp.""Name"", sp.""Slug"", sp.""IsPublished"", COUNT(s.""Id"")
                    FROM ""StatusPages"" sp
                    LEFT JOIN ""StatusPageServices"" s ON s.""StatusPageId"" = sp.""Id""
                    WHERE sp.""UserId"" = $1
                    GROUP BY sp.""Id""
                    ORDER BY sp.""CreatedAt"" DESC;";
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    pages.Add(new StatusPageSummaryResponse(
                        reader.GetGuid(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetBoolean(3),
                        (int)reader.GetInt64(4)
                    ));
                }

                return Results.Ok(pages);
            });

            group.MapPost("/", async (CreateStatusPageRequest request, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                if (!TryGetUserId(context, out var userId))
                {
                    return Results.Unauthorized();
                }

                if (!ValidateStatusPageInput(request.Name, request.Slug, out var validationError))
                {
                    return Results.BadRequest(validationError);
                }

                await using var connection = await dataSource.OpenConnectionAsync();

                if (await SlugTakenAsync(connection, request.Slug, null))
                {
                    return Results.Conflict("That slug is already in use.");
                }

                var pageId = Guid.NewGuid();
                var now = DateTime.UtcNow;

                await using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO ""StatusPages"" (""Id"", ""UserId"", ""Name"", ""Slug"", ""IsPublished"", ""CreatedAt"", ""UpdatedAt"")
                    VALUES ($1, $2, $3, $4, false, $5, $5);";

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = pageId });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = request.Name });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = request.Slug });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = now });

                await command.ExecuteNonQueryAsync();

                var response = new StatusPageResponse(pageId, request.Name, request.Slug, false, now);
                return Results.Created($"/api/status-pages/{pageId}", response);
            });

            group.MapGet("/{id:guid}", async (Guid id, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                if (!TryGetUserId(context, out var userId))
                {
                    return Results.Unauthorized();
                }

                await using var connection = await dataSource.OpenConnectionAsync();

                string name, slug;
                bool isPublished;

                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        SELECT ""Name"", ""Slug"", ""IsPublished""
                        FROM ""StatusPages""
                        WHERE ""Id"" = $1 AND ""UserId"" = $2;";
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                    await using var reader = await command.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return Results.NotFound();
                    }

                    name = reader.GetString(0);
                    slug = reader.GetString(1);
                    isPublished = reader.GetBoolean(2);
                }

                var services = await LoadServicesAsync(connection, id);

                return Results.Ok(new StatusPageDetailResponse(id, name, slug, isPublished, services));
            });

            group.MapPatch("/{id:guid}", async (Guid id, UpdateStatusPageRequest request, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                if (!TryGetUserId(context, out var userId))
                {
                    return Results.Unauthorized();
                }

                if (!ValidateStatusPageInput(request.Name, request.Slug, out var validationError))
                {
                    return Results.BadRequest(validationError);
                }

                await using var connection = await dataSource.OpenConnectionAsync();

                if (await SlugTakenAsync(connection, request.Slug, id))
                {
                    return Results.Conflict("That slug is already in use.");
                }

                await using var command = connection.CreateCommand();
                command.CommandText = @"
                    UPDATE ""StatusPages""
                    SET ""Name"" = $1, ""Slug"" = $2, ""IsPublished"" = $3, ""UpdatedAt"" = $4
                    WHERE ""Id"" = $5 AND ""UserId"" = $6
                    RETURNING ""UpdatedAt"";";

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = request.Name });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = request.Slug });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = request.IsPublished });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = DateTime.UtcNow });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                var result = await command.ExecuteScalarAsync();
                if (result is not DateTime updatedAt)
                {
                    return Results.NotFound();
                }

                return Results.Ok(new StatusPageResponse(id, request.Name, request.Slug, request.IsPublished, updatedAt));
            });

            group.MapDelete("/{id:guid}", async (Guid id, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                if (!TryGetUserId(context, out var userId))
                {
                    return Results.Unauthorized();
                }

                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM \"StatusPages\" WHERE \"Id\" = $1 AND \"UserId\" = $2";
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                var rowsAffected = await command.ExecuteNonQueryAsync();
                return rowsAffected == 0 ? Results.NotFound() : Results.NoContent();
            });

            group.MapPost("/{id:guid}/services", async (Guid id, AttachServiceRequest request, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                if (!TryGetUserId(context, out var userId))
                {
                    return Results.Unauthorized();
                }

                if (string.IsNullOrWhiteSpace(request.DisplayName))
                {
                    return Results.BadRequest("Display name cannot be empty.");
                }

                await using var connection = await dataSource.OpenConnectionAsync();

                if (!await OwnsStatusPageAsync(connection, id, userId))
                {
                    return Results.NotFound();
                }

                // The monitor being attached must belong to the same user - otherwise a status
                // page could expose someone else's monitor's uptime status.
                await using (var ownershipCommand = connection.CreateCommand())
                {
                    ownershipCommand.CommandText = "SELECT 1 FROM \"MonitorTargets\" WHERE \"Id\" = $1 AND \"UserId\" = $2";
                    ownershipCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = request.MonitorId });
                    ownershipCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                    var owned = await ownershipCommand.ExecuteScalarAsync();
                    if (owned is null)
                    {
                        return Results.BadRequest("Monitor not found.");
                    }
                }

                var serviceId = Guid.NewGuid();

                await using (var insertCommand = connection.CreateCommand())
                {
                    insertCommand.CommandText = @"
                        INSERT INTO ""StatusPageServices"" (""Id"", ""StatusPageId"", ""MonitorId"", ""DisplayName"", ""SectionName"", ""SortOrder"")
                        VALUES ($1, $2, $3, $4, $5, (
                            SELECT COALESCE(MAX(""SortOrder""), -1) + 1 FROM ""StatusPageServices"" WHERE ""StatusPageId"" = $2
                        ));";

                    insertCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = serviceId });
                    insertCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                    insertCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = request.MonitorId });
                    insertCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = request.DisplayName });
                    insertCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)request.SectionName ?? DBNull.Value });

                    try
                    {
                        await insertCommand.ExecuteNonQueryAsync();
                    }
                    catch (PostgresException ex) when (ex.SqlState == "23505")
                    {
                        return Results.Conflict("That monitor is already on this status page.");
                    }
                }

                var status = await GetMonitorStatusAsync(connection, request.MonitorId);
                var response = new StatusPageServiceResponse(serviceId, request.MonitorId, request.DisplayName, request.SectionName, 0, status);
                return Results.Created($"/api/status-pages/{id}/services/{serviceId}", response);
            });

            group.MapPatch("/{id:guid}/services/{serviceId:guid}", async (Guid id, Guid serviceId, UpdateServiceRequest request, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                if (!TryGetUserId(context, out var userId))
                {
                    return Results.Unauthorized();
                }

                if (string.IsNullOrWhiteSpace(request.DisplayName))
                {
                    return Results.BadRequest("Display name cannot be empty.");
                }

                await using var connection = await dataSource.OpenConnectionAsync();

                if (!await OwnsStatusPageAsync(connection, id, userId))
                {
                    return Results.NotFound();
                }

                await using var command = connection.CreateCommand();
                command.CommandText = @"
                    UPDATE ""StatusPageServices""
                    SET ""DisplayName"" = $1, ""SectionName"" = $2
                    WHERE ""Id"" = $3 AND ""StatusPageId"" = $4
                    RETURNING ""MonitorId"", ""SortOrder"";";

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = request.DisplayName });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)request.SectionName ?? DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = serviceId });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });

                await using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return Results.NotFound();
                }

                var monitorId = reader.GetGuid(0);
                var sortOrder = reader.GetInt32(1);
                await reader.CloseAsync();

                var status = await GetMonitorStatusAsync(connection, monitorId);
                return Results.Ok(new StatusPageServiceResponse(serviceId, monitorId, request.DisplayName, request.SectionName, sortOrder, status));
            });

            group.MapDelete("/{id:guid}/services/{serviceId:guid}", async (Guid id, Guid serviceId, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                if (!TryGetUserId(context, out var userId))
                {
                    return Results.Unauthorized();
                }

                await using var connection = await dataSource.OpenConnectionAsync();

                if (!await OwnsStatusPageAsync(connection, id, userId))
                {
                    return Results.NotFound();
                }

                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM \"StatusPageServices\" WHERE \"Id\" = $1 AND \"StatusPageId\" = $2";
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = serviceId });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });

                var rowsAffected = await command.ExecuteNonQueryAsync();
                return rowsAffected == 0 ? Results.NotFound() : Results.NoContent();
            });

            group.MapPut("/{id:guid}/services/reorder", async (Guid id, ReorderServicesRequest request, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                if (!TryGetUserId(context, out var userId))
                {
                    return Results.Unauthorized();
                }

                await using var connection = await dataSource.OpenConnectionAsync();

                if (!await OwnsStatusPageAsync(connection, id, userId))
                {
                    return Results.NotFound();
                }

                await using var transaction = await connection.BeginTransactionAsync();

                foreach (var entry in request.Services)
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"
                        UPDATE ""StatusPageServices""
                        SET ""SectionName"" = $1, ""SortOrder"" = $2
                        WHERE ""Id"" = $3 AND ""StatusPageId"" = $4;";

                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)entry.SectionName ?? DBNull.Value });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = entry.SortOrder });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = entry.ServiceId });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });

                    await command.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();

                var services = await LoadServicesAsync(connection, id);
                return Results.Ok(services);
            });

            return endpoints;
        }

        private static bool TryGetUserId(HttpContext context, out Guid userId)
        {
            var claim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? context.User.FindFirst("sub")?.Value;

            return Guid.TryParse(claim, out userId);
        }

        private static bool ValidateStatusPageInput(string name, string slug, out string? error)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "Name cannot be empty.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(slug) || slug.Length > 64 ||
                !System.Text.RegularExpressions.Regex.IsMatch(slug, "^[a-z0-9-]+$"))
            {
                error = "Slug must be lowercase letters, numbers, and hyphens only.";
                return false;
            }

            error = null;
            return true;
        }

        private static async System.Threading.Tasks.Task<bool> SlugTakenAsync(NpgsqlConnection connection, string slug, Guid? excludingPageId)
        {
            // /hub/:slug and /status/:slug are disjoint route prefixes, but both draw from a
            // human-typed slug, so we still block collisions against MonitorTargets.PublicSlug
            // to avoid two different "your slug is taken" surprises for the same word.
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT 1 FROM \"MonitorTargets\" WHERE \"PublicSlug\" = $1";
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = slug });
                if (await command.ExecuteScalarAsync() is not null)
                {
                    return true;
                }
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = excludingPageId is null
                    ? "SELECT 1 FROM \"StatusPages\" WHERE \"Slug\" = $1"
                    : "SELECT 1 FROM \"StatusPages\" WHERE \"Slug\" = $1 AND \"Id\" != $2";

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = slug });
                if (excludingPageId is not null)
                {
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = excludingPageId.Value });
                }

                return await command.ExecuteScalarAsync() is not null;
            }
        }

        private static async System.Threading.Tasks.Task<bool> OwnsStatusPageAsync(NpgsqlConnection connection, Guid pageId, Guid userId)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM \"StatusPages\" WHERE \"Id\" = $1 AND \"UserId\" = $2";
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = pageId });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });
            return await command.ExecuteScalarAsync() is not null;
        }

        private static async System.Threading.Tasks.Task<UptimeStatus> GetMonitorStatusAsync(NpgsqlConnection connection, Guid monitorId)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"CurrentUptimeStatus\" FROM \"MonitorTargets\" WHERE \"Id\" = $1";
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = monitorId });
            var result = await command.ExecuteScalarAsync();
            return result is int statusValue ? (UptimeStatus)statusValue : UptimeStatus.Healthy;
        }

        private static async System.Threading.Tasks.Task<List<StatusPageServiceResponse>> LoadServicesAsync(NpgsqlConnection connection, Guid pageId)
        {
            var services = new List<StatusPageServiceResponse>();

            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT s.""Id"", s.""MonitorId"", s.""DisplayName"", s.""SectionName"", s.""SortOrder"", mt.""CurrentUptimeStatus""
                FROM ""StatusPageServices"" s
                INNER JOIN ""MonitorTargets"" mt ON mt.""Id"" = s.""MonitorId""
                WHERE s.""StatusPageId"" = $1
                ORDER BY s.""SectionName"" NULLS FIRST, s.""SortOrder"";";
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = pageId });

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                services.Add(new StatusPageServiceResponse(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt32(4),
                    (UptimeStatus)reader.GetInt32(5)
                ));
            }

            return services;
        }
    }
}

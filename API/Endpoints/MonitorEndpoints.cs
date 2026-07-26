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
                command.CommandText = "SELECT \"Id\", \"Url\", \"FriendlyName\", \"CurrentUptimeStatus\", \"CurrentSecurityGrade\", \"IsActive\", \"MonitorType\" FROM \"MonitorTargets\" WHERE \"UserId\" = $1";
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
                        reader.GetBoolean(5),
                        (MonitorType)reader.GetInt32(6)
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

                string monitorUrl;

                if (request.MonitorType == MonitorType.Journey)
                {
                    if (!ValidateJourneySteps(request.Steps, out var stepsError))
                    {
                        return Results.BadRequest(stepsError);
                    }

                    if (string.IsNullOrWhiteSpace(request.FriendlyName))
                    {
                        return Results.BadRequest("Friendly Name cannot be empty.");
                    }

                    // Journeys never collect a top-level URL from the UI - it's derived from step 1
                    // so the monitor can still be graded/cert-inspected the same way classic monitors are.
                    monitorUrl = request.Steps![0].Url;
                }
                else
                {
                    if (!ValidateMonitorInput(request.Url, request.FriendlyName, out var validationError))
                    {
                        return Results.BadRequest(validationError);
                    }

                    monitorUrl = request.Url!;
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
                await using var transaction = await connection.BeginTransactionAsync();

                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
                        INSERT INTO ""MonitorTargets"" (""Id"", ""UserId"", ""Url"", ""FriendlyName"", ""IntervalMinutes"", ""RequestTimeout"", ""IsActive"", ""CurrentUptimeStatus"", ""CurrentSecurityGrade"", ""UpdatedAt"", ""MonitorType"")
                        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
                        RETURNING ""Id"";";

                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = monitorId });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = monitorUrl });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = request.FriendlyName });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = 10 });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = 30 });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = true });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = 0 });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "U" });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = DateTime.UtcNow });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = (int)request.MonitorType });

                    await command.ExecuteScalarAsync();
                }

                if (request.MonitorType == MonitorType.Journey)
                {
                    await InsertJourneyStepsAsync(connection, transaction, monitorId, request.Steps!);
                }

                await transaction.CommitAsync();

                var response = new MonitorResponse(
                    monitorId,
                    monitorUrl,
                    request.FriendlyName,
                    (UptimeStatus)0,
                    'U',
                    true,
                    request.MonitorType
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

            group.MapGet("/{id:guid}/copilot", async (Guid id, [FromServices] NpgsqlDataSource dataSource, [FromServices] KindleKeep.Api.Infrastructure.Ai.GroqClient groqClient, [FromServices] ILogger<KindleKeep.Api.Infrastructure.Ai.GroqClient> logger, HttpContext context, CancellationToken cancellationToken) =>
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
                {
                    return Results.Unauthorized();
                }

                await using var connection = await dataSource.OpenConnectionAsync();

                Guid auditId;
                bool hasCsp, hasHsts, hasXfo, hasNosniff;
                string? rawHeaders;
                string? cachedExplanation;
                string friendlyName;
                char grade;
                string? sslIssuer;
                DateTime? sslExpiryAt;
                string? tlsVersion;

                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
                        SELECT sa.""Id"", sa.""HasCsp"", sa.""HasHsts"", sa.""HasXfo"", sa.""HasNosniff"", sa.""RawHeaders"", sa.""CopilotExplanation"",
                               mt.""FriendlyName"", mt.""CurrentSecurityGrade"", sa.""SslIssuer"", sa.""SslExpiryAt"", sa.""TlsVersion""
                        FROM ""SecurityAudits"" sa
                        INNER JOIN ""MonitorTargets"" mt ON sa.""MonitorId"" = mt.""Id""
                        WHERE mt.""Id"" = $1 AND mt.""UserId"" = $2
                        ORDER BY sa.""CreatedAt"" DESC
                        LIMIT 1;";

                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    if (!await reader.ReadAsync(cancellationToken))
                    {
                        return Results.NotFound();
                    }

                    auditId = reader.GetGuid(0);
                    hasCsp = reader.GetBoolean(1);
                    hasHsts = reader.GetBoolean(2);
                    hasXfo = reader.GetBoolean(3);
                    hasNosniff = reader.GetBoolean(4);
                    rawHeaders = reader.IsDBNull(5) ? null : reader.GetString(5);
                    cachedExplanation = reader.IsDBNull(6) ? null : reader.GetString(6);
                    friendlyName = reader.GetString(7);
                    grade = reader.GetString(8)[0];
                    sslIssuer = reader.IsDBNull(9) ? null : reader.GetString(9);
                    sslExpiryAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10);
                    tlsVersion = reader.IsDBNull(11) ? null : reader.GetString(11);
                }

                var missingHeaders = new List<string>();
                if (!hasCsp) missingHeaders.Add("Content-Security-Policy");
                if (!hasHsts) missingHeaders.Add("Strict-Transport-Security");
                if (!hasXfo) missingHeaders.Add("X-Frame-Options");
                if (!hasNosniff) missingHeaders.Add("X-Content-Type-Options");

                // Grade is clean - nothing to explain, and no reason to spend a Gemini call or
                // count against the rate limit for a monitor that has no findings.
                if (missingHeaders.Count == 0)
                {
                    return Results.Ok(new CopilotResponse(null, null, null));
                }

                string? detectedPlatform = null;
                string? remediationSnippet = null;
                if (rawHeaders is not null)
                {
                    var headersDict = JsonSerializer.Deserialize(rawHeaders, AppJsonSerializerContext.Default.DictionaryStringString)
                        ?? [];
                    detectedPlatform = BlueprintGenerator.DetectPlatform(headersDict);
                    remediationSnippet = BlueprintGenerator.GenerateSnippet(detectedPlatform, missingHeaders);
                }

                // Cached from a prior call against this exact audit row - free, and checked before
                // the rate limiter would even matter for repeat views of the same audit.
                if (cachedExplanation is not null)
                {
                    return Results.Ok(new CopilotResponse(cachedExplanation, detectedPlatform, remediationSnippet));
                }

                string? explanation;
                try
                {
                    var presentHeaders = new List<string>();
                    if (hasCsp) presentHeaders.Add("Content-Security-Policy");
                    if (hasHsts) presentHeaders.Add("Strict-Transport-Security");
                    if (hasXfo) presentHeaders.Add("X-Frame-Options");
                    if (hasNosniff) presentHeaders.Add("X-Content-Type-Options");

                    string? sslNote = null;
                    if (sslExpiryAt is not null)
                    {
                        var daysRemaining = (sslExpiryAt.Value - DateTime.UtcNow).TotalDays;
                        if (daysRemaining < 30)
                        {
                            sslNote = $" Its TLS certificate (issued by {sslIssuer ?? "an unknown issuer"}) {(daysRemaining < 0 ? "has already expired" : $"expires in {(int)daysRemaining} days")}.";
                        }
                    }

                    var prompt = $"You are a security assistant. Site \"{friendlyName}\" currently grades {grade} on a security scan, hosted on \"{detectedPlatform ?? "unknown"}\". " +
                        $"It's missing these HTTP security headers: {string.Join(", ", missingHeaders)}." +
                        (presentHeaders.Count > 0 ? $" It already has: {string.Join(", ", presentHeaders)}." : "") +
                        sslNote +
                        " In 2-3 plain-language sentences, explain the practical risk of the missing headers specifically for this site's grade and platform. Do not include code or configuration syntax - that is provided separately.";
                    explanation = await groqClient.GenerateExplanationAsync(prompt, cancellationToken);

                    await using var updateCommand = connection.CreateCommand();
                    updateCommand.CommandText = @"UPDATE ""SecurityAudits"" SET ""CopilotExplanation"" = $1, ""CopilotGeneratedAt"" = $2 WHERE ""Id"" = $3;";
                    updateCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = explanation });
                    updateCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = DateTime.UtcNow });
                    updateCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = auditId });
                    await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    // Gemini errored, timed out, or hit its own free-tier rate limit - fall back to
                    // the deterministic snippet rather than making this request fail outright.
                    logger.LogWarning(ex, "Copilot generation failed for monitor {MonitorId}", id);
                    explanation = null;
                }

                return Results.Ok(new CopilotResponse(explanation, detectedPlatform, remediationSnippet));
            }).RequireRateLimiting("copilot");

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
                           ""CurrentUptimeStatus"", ""CurrentSecurityGrade"", ""IsActive"", ""IsPublic"", ""PublicSlug"", ""MonitorType""
                    FROM ""MonitorTargets""
                    WHERE ""Id"" = $1 AND ""UserId"" = $2;";

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                await using var reader = await command.ExecuteReaderAsync();

                if (!await reader.ReadAsync())
                {
                    return Results.NotFound();
                }

                var monitorType = (MonitorType)reader.GetInt32(11);

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
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    monitorType,
                    monitorType == MonitorType.Journey ? await LoadJourneyStepsAsync(dataSource, id) : null
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

                await using var connection = await dataSource.OpenConnectionAsync();

                MonitorType currentType;
                await using (var typeCommand = connection.CreateCommand())
                {
                    typeCommand.CommandText = "SELECT \"MonitorType\" FROM \"MonitorTargets\" WHERE \"Id\" = $1 AND \"UserId\" = $2";
                    typeCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                    typeCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                    var result = await typeCommand.ExecuteScalarAsync();
                    if (result is not int typeValue)
                    {
                        return Results.NotFound();
                    }
                    currentType = (MonitorType)typeValue;
                }

                string monitorUrl;
                string? requestHeadersJson = null;

                if (currentType == MonitorType.Journey)
                {
                    if (!ValidateJourneySteps(request.Steps, out var stepsError))
                    {
                        return Results.BadRequest(stepsError);
                    }

                    if (string.IsNullOrWhiteSpace(request.FriendlyName))
                    {
                        return Results.BadRequest("Friendly Name cannot be empty.");
                    }

                    monitorUrl = request.Steps![0].Url;
                }
                else
                {
                    if (!ValidateMonitorInput(request.Url, request.FriendlyName, out var validationError))
                    {
                        return Results.BadRequest(validationError);
                    }

                    monitorUrl = request.Url!;
                    requestHeadersJson = request.RequestHeaders is null
                        ? null
                        : JsonSerializer.Serialize(request.RequestHeaders, AppJsonSerializerContext.Default.DictionaryStringString);
                }

                var intervalMinutes = Math.Max(1, request.IntervalMinutes);
                var requestTimeout = Math.Clamp(request.RequestTimeout, 1, 60);

                await using var transaction = await connection.BeginTransactionAsync();

                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
                        UPDATE ""MonitorTargets""
                        SET ""Url"" = $1, ""FriendlyName"" = $2, ""IntervalMinutes"" = $3, ""RequestTimeout"" = $4,
                            ""RequestHeaders"" = $5, ""UpdatedAt"" = $6
                        WHERE ""Id"" = $7 AND ""UserId"" = $8;";

                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = monitorUrl });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = request.FriendlyName });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = intervalMinutes });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = requestTimeout });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = (object?)requestHeadersJson ?? DBNull.Value });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = DateTime.UtcNow });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                    await command.ExecuteNonQueryAsync();
                }

                if (currentType == MonitorType.Journey)
                {
                    // Steps are replaced wholesale on every save - they're edited as one ordered
                    // array in a single builder UI, never independently CRUD'd.
                    await using (var deleteCommand = connection.CreateCommand())
                    {
                        deleteCommand.Transaction = transaction;
                        deleteCommand.CommandText = "DELETE FROM \"JourneySteps\" WHERE \"MonitorId\" = $1";
                        deleteCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                        await deleteCommand.ExecuteNonQueryAsync();
                    }

                    await InsertJourneyStepsAsync(connection, transaction, id, request.Steps!);
                }

                await transaction.CommitAsync();

                await using var readConnection = await dataSource.OpenConnectionAsync();
                await using var readCommand = readConnection.CreateCommand();
                readCommand.CommandText = @"
                    SELECT ""Id"", ""Url"", ""FriendlyName"", ""IntervalMinutes"", ""RequestTimeout"", ""RequestHeaders"",
                           ""CurrentUptimeStatus"", ""CurrentSecurityGrade"", ""IsActive"", ""IsPublic"", ""PublicSlug""
                    FROM ""MonitorTargets""
                    WHERE ""Id"" = $1;";
                readCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });

                await using var reader = await readCommand.ExecuteReaderAsync();
                await reader.ReadAsync();

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
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    currentType,
                    currentType == MonitorType.Journey ? await LoadJourneyStepsAsync(dataSource, id) : null
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

        private static bool ValidateMonitorInput(string? url, string friendlyName, out string? error)
        {
            if (string.IsNullOrWhiteSpace(friendlyName))
            {
                error = "Friendly Name cannot be empty.";
                return false;
            }

            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uriResult) ||
                (uriResult.Scheme != Uri.UriSchemeHttp && uriResult.Scheme != Uri.UriSchemeHttps))
            {
                error = "Invalid URL format. Must be an absolute HTTP or HTTPS URI.";
                return false;
            }

            error = null;
            return true;
        }

        private const int MaxJourneySteps = 10;

        private static bool ValidateJourneySteps(List<JourneyStepDto>? steps, out string? error)
        {
            if (steps is null || steps.Count == 0)
            {
                error = "A journey requires at least one step.";
                return false;
            }

            if (steps.Count > MaxJourneySteps)
            {
                error = $"A journey supports at most {MaxJourneySteps} steps.";
                return false;
            }

            var capturedSoFar = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];

                if (string.IsNullOrWhiteSpace(step.Method))
                {
                    error = $"Step {i + 1}: method is required.";
                    return false;
                }

                if (!Uri.TryCreate(step.Url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    error = $"Step {i + 1}: URL must be an absolute HTTP or HTTPS URI.";
                    return false;
                }

                var referenced = new List<string>();
                CollectVarRefs(step.Url, referenced);
                if (step.Headers is not null)
                {
                    foreach (var value in step.Headers.Values) CollectVarRefs(value, referenced);
                }
                if (step.Body is not null) CollectVarRefs(step.Body, referenced);

                foreach (var varName in referenced)
                {
                    if (!capturedSoFar.Contains(varName))
                    {
                        error = $"Step {i + 1} references undefined variable '{{{{{varName}}}}}' - it must be captured by an earlier step.";
                        return false;
                    }
                }

                if (step.CaptureAs is not null)
                {
                    if (step.CaptureJsonPath is null)
                    {
                        error = $"Step {i + 1}: captureAs requires captureJsonPath.";
                        return false;
                    }

                    if (!capturedSoFar.Add(step.CaptureAs))
                    {
                        error = $"Step {i + 1}: capture variable '{step.CaptureAs}' is already used by an earlier step.";
                        return false;
                    }
                }

                if (step.AssertJsonPath is not null && step.AssertEquals is null)
                {
                    error = $"Step {i + 1}: assertJsonPath requires assertEquals.";
                    return false;
                }
            }

            error = null;
            return true;
        }

        private static void CollectVarRefs(string input, List<string> into)
        {
            var idx = 0;
            while (true)
            {
                var start = input.IndexOf("{{", idx, StringComparison.Ordinal);
                if (start < 0) break;
                var end = input.IndexOf("}}", start + 2, StringComparison.Ordinal);
                if (end < 0) break;
                into.Add(input[(start + 2)..end]);
                idx = end + 2;
            }
        }

        private static async System.Threading.Tasks.Task InsertJourneyStepsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid monitorId, List<JourneyStepDto> steps)
        {
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];

                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO ""JourneySteps""
                        (""Id"", ""MonitorId"", ""StepOrder"", ""Method"", ""UrlOrPath"", ""Headers"", ""Body"", ""CaptureAs"", ""CaptureJsonPath"", ""AssertJsonPath"", ""AssertEquals"", ""ExpectedStatusCode"")
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12);";

                var headersJson = step.Headers is null
                    ? null
                    : JsonSerializer.Serialize(step.Headers, AppJsonSerializerContext.Default.DictionaryStringString);

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.NewGuid() });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = monitorId });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = i });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = step.Method.ToUpperInvariant() });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = step.Url });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)headersJson ?? DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)step.Body ?? DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)step.CaptureAs ?? DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)step.CaptureJsonPath ?? DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)step.AssertJsonPath ?? DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)step.AssertEquals ?? DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = (object?)step.ExpectedStatusCode ?? DBNull.Value });

                await command.ExecuteNonQueryAsync();
            }
        }

        private static async System.Threading.Tasks.Task<List<JourneyStepDto>> LoadJourneyStepsAsync(NpgsqlDataSource dataSource, Guid monitorId)
        {
            var steps = new List<JourneyStepDto>();

            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT ""StepOrder"", ""Method"", ""UrlOrPath"", ""Headers"", ""Body"", ""CaptureAs"", ""CaptureJsonPath"", ""AssertJsonPath"", ""AssertEquals"", ""ExpectedStatusCode""
                FROM ""JourneySteps""
                WHERE ""MonitorId"" = $1
                ORDER BY ""StepOrder"";";
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = monitorId });

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var headers = reader.IsDBNull(3)
                    ? null
                    : JsonSerializer.Deserialize(reader.GetString(3), AppJsonSerializerContext.Default.DictionaryStringString);

                steps.Add(new JourneyStepDto(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    headers,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetInt32(9)));
            }

            return steps;
        }
    }
}
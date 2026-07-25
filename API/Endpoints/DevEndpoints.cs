using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using KindleKeep.Api.Core.DTOs;
using KindleKeep.Api.Infrastructure.BackgroundServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;

namespace KindleKeep.Api.Core.DTOs
{
    public record CreateApiKeyRequest([property: JsonPropertyName("label")] string Label);

    public record ApiKeyResponse(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
        [property: JsonPropertyName("lastUsedAt")] DateTime? LastUsedAt
    );

    public record ApiKeyCreatedResponse(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("rawKey")] string RawKey,
        [property: JsonPropertyName("createdAt")] DateTime CreatedAt
    );

    public record WebhookSecretResponse(
        [property: JsonPropertyName("secret")] string Secret
    );

    public record TriggerProbeResponse(
        [property: JsonPropertyName("triggered")] bool Triggered
    );
}

namespace KindleKeep.Api.API.Endpoints
{
    public static class DevEndpoints
    {
        public static IEndpointRouteBuilder MapDevEndpoints(this IEndpointRouteBuilder endpoints)
        {
            var group = endpoints.MapGroup("/api/dev").RequireAuthorization();

            group.MapGet("/keys", async ([FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
                {
                    return Results.Unauthorized();
                }

                var keys = new List<ApiKeyResponse>();

                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT ""Id"", ""Label"", ""CreatedAt"", ""LastUsedAt""
                    FROM ""ApiKeys"" WHERE ""UserId"" = $1 ORDER BY ""CreatedAt"" DESC;";
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    keys.Add(new ApiKeyResponse(
                        reader.GetGuid(0),
                        reader.GetString(1),
                        reader.GetDateTime(2),
                        reader.IsDBNull(3) ? null : reader.GetDateTime(3)));
                }

                return Results.Ok(keys);
            });

            group.MapPost("/keys", async (CreateApiKeyRequest request, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
                {
                    return Results.Unauthorized();
                }

                if (string.IsNullOrWhiteSpace(request.Label))
                {
                    return Results.BadRequest("Label is required.");
                }

                // Raw key is returned to the caller exactly once here and never again -
                // only its SHA-256 hash is persisted, matching a standard API-key pattern.
                var rawKey = $"kk_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}";
                var keyHash = ComputeHash(rawKey);
                var id = Guid.NewGuid();
                var createdAt = DateTime.UtcNow;

                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO ""ApiKeys"" (""Id"", ""UserId"", ""KeyHash"", ""Label"", ""CreatedAt"")
                    VALUES ($1, $2, $3, $4, $5);";
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = keyHash });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = request.Label });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = createdAt });

                await command.ExecuteNonQueryAsync();

                return Results.Ok(new ApiKeyCreatedResponse(id, request.Label, rawKey, createdAt));
            });

            group.MapDelete("/keys/{id:guid}", async (Guid id, [FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
                {
                    return Results.Unauthorized();
                }

                await using var connection = await dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = @"DELETE FROM ""ApiKeys"" WHERE ""Id"" = $1 AND ""UserId"" = $2;";
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                var rowsAffected = await command.ExecuteNonQueryAsync();

                return rowsAffected == 0 ? Results.NotFound("Key not found.") : Results.NoContent();
            });

            group.MapGet("/webhook-secret", async ([FromServices] NpgsqlDataSource dataSource, HttpContext context) =>
            {
                var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? context.User.FindFirst("sub")?.Value;

                if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
                {
                    return Results.Unauthorized();
                }

                await using var connection = await dataSource.OpenConnectionAsync();

                string? secret;
                await using (var selectCommand = connection.CreateCommand())
                {
                    selectCommand.CommandText = @"SELECT ""GithubWebhookSecret"" FROM ""Users"" WHERE ""Id"" = $1;";
                    selectCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });

                    await using var reader = await selectCommand.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return Results.NotFound("User not found.");
                    }
                    secret = reader.IsDBNull(0) ? null : reader.GetString(0);
                }

                if (secret is null)
                {
                    secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

                    await using var updateCommand = connection.CreateCommand();
                    updateCommand.CommandText = @"UPDATE ""Users"" SET ""GithubWebhookSecret"" = $1 WHERE ""Id"" = $2;";
                    updateCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = secret });
                    updateCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });
                    await updateCommand.ExecuteNonQueryAsync();
                }

                return Results.Ok(new WebhookSecretResponse(secret));
            });

            // Not JWT-gated - authenticated via the X-Api-Key header instead (hashed + looked up below).
            endpoints.MapPost("/api/v1/trigger/{monitorId:guid}", async (
                Guid monitorId,
                HttpContext context,
                [FromServices] NpgsqlDataSource dataSource,
                [FromServices] WatcherEngine watcherEngine,
                CancellationToken ct) =>
            {
                if (!context.Request.Headers.TryGetValue("X-Api-Key", out var apiKeyHeader) || string.IsNullOrWhiteSpace(apiKeyHeader))
                {
                    return Results.Unauthorized();
                }

                var keyHash = ComputeHash(apiKeyHeader.ToString());

                await using var connection = await dataSource.OpenConnectionAsync(ct);

                Guid? ownerUserId = null;
                Guid? apiKeyId = null;
                await using (var lookupCommand = connection.CreateCommand())
                {
                    lookupCommand.CommandText = @"SELECT ""Id"", ""UserId"" FROM ""ApiKeys"" WHERE ""KeyHash"" = $1;";
                    lookupCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = keyHash });

                    await using var reader = await lookupCommand.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        apiKeyId = reader.GetGuid(0);
                        ownerUserId = reader.GetGuid(1);
                    }
                }

                if (ownerUserId is null)
                {
                    return Results.Unauthorized();
                }

                await using (var ownershipCommand = connection.CreateCommand())
                {
                    ownershipCommand.CommandText = @"SELECT 1 FROM ""MonitorTargets"" WHERE ""Id"" = $1 AND ""UserId"" = $2;";
                    ownershipCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = monitorId });
                    ownershipCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = ownerUserId.Value });

                    var owns = await ownershipCommand.ExecuteScalarAsync(ct);
                    if (owns is null)
                    {
                        return Results.NotFound();
                    }
                }

                await using (var touchCommand = connection.CreateCommand())
                {
                    touchCommand.CommandText = @"UPDATE ""ApiKeys"" SET ""LastUsedAt"" = $1 WHERE ""Id"" = $2;";
                    touchCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = DateTime.UtcNow });
                    touchCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = apiKeyId!.Value });
                    await touchCommand.ExecuteNonQueryAsync(ct);
                }

                var triggered = await watcherEngine.TriggerImmediateProbeAsync(monitorId, ct);
                return Results.Ok(new TriggerProbeResponse(triggered));
            });

            // Not JWT-gated - authenticated via HMAC-SHA256 signature (X-Hub-Signature-256, GitHub's convention).
            endpoints.MapPost("/api/webhooks/github/{monitorId:guid}", async (
                Guid monitorId,
                HttpContext context,
                [FromServices] NpgsqlDataSource dataSource,
                [FromServices] WatcherEngine watcherEngine,
                CancellationToken ct) =>
            {
                if (!context.Request.Headers.TryGetValue("X-Hub-Signature-256", out var signatureHeader) ||
                    !signatureHeader.ToString().StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Unauthorized();
                }

                using var bodyReader = new StreamReader(context.Request.Body);
                var rawBody = await bodyReader.ReadToEndAsync(ct);

                await using var connection = await dataSource.OpenConnectionAsync(ct);

                string? secret = null;
                Guid? ownerUserId = null;
                await using (var lookupCommand = connection.CreateCommand())
                {
                    lookupCommand.CommandText = @"
                        SELECT u.""GithubWebhookSecret"", u.""Id""
                        FROM ""MonitorTargets"" mt
                        INNER JOIN ""Users"" u ON mt.""UserId"" = u.""Id""
                        WHERE mt.""Id"" = $1;";
                    lookupCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = monitorId });

                    await using var reader = await lookupCommand.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        secret = reader.IsDBNull(0) ? null : reader.GetString(0);
                        ownerUserId = reader.GetGuid(1);
                    }
                }

                if (ownerUserId is null || string.IsNullOrEmpty(secret))
                {
                    return Results.NotFound();
                }

                var expectedHex = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(rawBody)));
                var providedHex = signatureHeader.ToString()["sha256=".Length..];

                var expectedBytes = Encoding.UTF8.GetBytes(expectedHex);
                var providedBytes = Encoding.UTF8.GetBytes(providedHex);

                if (expectedBytes.Length != providedBytes.Length || !CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
                {
                    return Results.Unauthorized();
                }

                // GitHub always sends X-GitHub-Event; ack (but skip triggering) events we don't care about,
                // like "ping" sent when the webhook is first configured.
                var eventType = context.Request.Headers.TryGetValue("X-GitHub-Event", out var eventHeader) ? eventHeader.ToString() : null;
                if (eventType is not ("push" or "deployment"))
                {
                    return Results.Ok(new TriggerProbeResponse(false));
                }

                var triggered = await watcherEngine.TriggerImmediateProbeAsync(monitorId, ct);
                return Results.Ok(new TriggerProbeResponse(triggered));
            });

            return endpoints;
        }

        private static string ComputeHash(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}

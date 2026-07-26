using KindleKeep.Api.Infrastructure.Alerting;
using Npgsql;
using NpgsqlTypes;

namespace KindleKeep.Api.Infrastructure.BackgroundServices;

// Polls for Uptime-Down incidents that have sat unacknowledged past the owning user's
// configured delay, and escalates each one exactly once via email. Deliberately independent
// of WatcherEngine/AlertManager's hot dispatch path - see AlertManager.DispatchEscalationEmailAsync.
public class EscalationService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = configuration.GetValue<int>("Escalation:IntervalMinutes", 1);
        var delay = TimeSpan.FromMinutes(intervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            await EscalateOverdueIncidentsAsync(stoppingToken);
            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task EscalateOverdueIncidentsAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var alertManager = scope.ServiceProvider.GetRequiredService<AlertManager>();

        var overdue = new List<(Guid IncidentId, string MonitorName, string MonitorUrl, string Email)>();

        await using (var connection = await dataSource.OpenConnectionAsync(stoppingToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT ai.""Id"", mt.""FriendlyName"", mt.""Url"", u.""Email""
                FROM ""AlertIncidents"" ai
                INNER JOIN ""MonitorTargets"" mt ON ai.""MonitorId"" = mt.""Id""
                INNER JOIN ""Users"" u ON mt.""UserId"" = u.""Id""
                WHERE ai.""IncidentType"" = 'Uptime'
                  AND ai.""IsResolved"" = false
                  AND ai.""AcknowledgedAt"" IS NULL
                  AND ai.""EscalatedAt"" IS NULL
                  AND ai.""CreatedAt"" <= now() - (u.""EscalationDelayMinutes"" || ' minutes')::interval;";

            await using var reader = await command.ExecuteReaderAsync(stoppingToken);
            while (await reader.ReadAsync(stoppingToken))
            {
                overdue.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        foreach (var (incidentId, monitorName, monitorUrl, email) in overdue)
        {
            await alertManager.DispatchEscalationEmailAsync(monitorName, monitorUrl, email, stoppingToken);

            await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
            await using var command = connection.CreateCommand();
            // The "EscalatedAt" IS NULL guard on the UPDATE itself (not just the SELECT above) is
            // what actually prevents a double-send if two poll cycles ever overlap on the same row.
            command.CommandText = @"
                UPDATE ""AlertIncidents""
                SET ""EscalatedAt"" = $1
                WHERE ""Id"" = $2 AND ""EscalatedAt"" IS NULL;";

            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = DateTime.UtcNow });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = incidentId });

            await command.ExecuteNonQueryAsync(stoppingToken);
        }
    }
}

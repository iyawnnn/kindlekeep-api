using KindleKeep.Api.Infrastructure.Alerting;
using Npgsql;
using NpgsqlTypes;

namespace KindleKeep.Api.Infrastructure.BackgroundServices;

public class DigestService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = configuration.GetValue<int>("Digest:IntervalHours", 24);
        var delay = TimeSpan.FromHours(intervalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            await SendDigestsAsync(stoppingToken);
            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task SendDigestsAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var alertManager = scope.ServiceProvider.GetRequiredService<AlertManager>();

        var recipients = new List<(Guid UserId, string Email)>();

        await using (var connection = await dataSource.OpenConnectionAsync(stoppingToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"SELECT ""Id"", ""Email"" FROM ""Users"" WHERE ""DigestEnabled"" = true";

            await using var reader = await command.ExecuteReaderAsync(stoppingToken);
            while (await reader.ReadAsync(stoppingToken))
            {
                recipients.Add((reader.GetGuid(0), reader.GetString(1)));
            }
        }

        if (recipients.Count == 0) return;

        var since = DateTime.UtcNow.AddHours(-24);

        foreach (var (userId, email) in recipients)
        {
            var incidents = new List<(string MonitorName, string IncidentType, DateTime CreatedAt)>();

            await using (var connection = await dataSource.OpenConnectionAsync(stoppingToken))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT mt.""FriendlyName"", ai.""IncidentType"", ai.""CreatedAt""
                    FROM ""AlertIncidents"" ai
                    INNER JOIN ""MonitorTargets"" mt ON ai.""MonitorId"" = mt.""Id""
                    WHERE mt.""UserId"" = $1 AND (ai.""IsResolved"" = false OR ai.""CreatedAt"" >= $2)
                    ORDER BY ai.""CreatedAt"" DESC";

                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = since });

                await using var reader = await command.ExecuteReaderAsync(stoppingToken);
                while (await reader.ReadAsync(stoppingToken))
                {
                    incidents.Add((reader.GetString(0), reader.GetString(1), reader.GetDateTime(2)));
                }
            }

            if (incidents.Count == 0) continue;

            await alertManager.DispatchDigestEmailAsync(email, incidents, stoppingToken);
        }
    }
}

using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using KindleKeep.Api.API.Hubs;
using KindleKeep.Api.Core.DTOs;
using KindleKeep.Api.Core.Entities;
using KindleKeep.Api.Core.Enums;
using KindleKeep.Api.Infrastructure.Alerting;
using Microsoft.AspNetCore.SignalR;
using Npgsql;
using NpgsqlTypes;

namespace KindleKeep.Api.Infrastructure.BackgroundServices;

public class WatcherEngine(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IHubContext<PulseHub> hubContext,
    IConfiguration configuration,
    AlertManager alertManager) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = configuration.GetValue<int>("Watcher:IntervalMinutes", 1);
        var delay = TimeSpan.FromMinutes(intervalMinutes);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ProcessTargetsAsync(stoppingToken);
                await Task.Delay(delay, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task StreamLogAsync(string monitorId, string message, CancellationToken stoppingToken)
    {
        await hubContext.Clients.Group(monitorId).SendAsync("ReceiveLogStream", message, stoppingToken);
    }

    private async Task ProcessTargetsAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var activeTargets = new List<(MonitorTarget Target, string? WebhookUrl)>();

        await using (var connection = await dataSource.OpenConnectionAsync(stoppingToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT mt.""Id"", mt.""Url"", mt.""FriendlyName"", mt.""CurrentUptimeStatus"", 
                       mt.""CurrentSecurityGrade"", mt.""IsActive"", mt.""UserId"", u.""DiscordWebhookUrl"", mt.""FailureCount"" 
                FROM ""MonitorTargets"" mt
                INNER JOIN ""Users"" u ON mt.""UserId"" = u.""Id""
                WHERE mt.""IsActive"" = true AND mt.""CurrentUptimeStatus"" != 3";
            
            await using var reader = await command.ExecuteReaderAsync(stoppingToken);
            while (await reader.ReadAsync(stoppingToken))
            {
                var target = new MonitorTarget
                {
                    Id = reader.GetGuid(0),
                    Url = reader.GetString(1),
                    FriendlyName = reader.GetString(2),
                    CurrentUptimeStatus = (UptimeStatus)reader.GetInt32(3),
                    CurrentSecurityGrade = reader.GetString(4)[0],
                    IsActive = reader.GetBoolean(5),
                    UserId = reader.GetGuid(6),
                    FailureCount = reader.GetInt32(8)
                };
                
                var webhookUrl = reader.IsDBNull(7) ? null : reader.GetString(7);
                activeTargets.Add((target, webhookUrl));
            }
        }

        var client = httpClientFactory.CreateClient("WatcherClient");

        foreach (var (target, webhookUrl) in activeTargets)
        {
            var stopwatch = new Stopwatch();
            var status = UptimeStatus.Down;
            string? errorMessage = null;
            int? statusCode = null;
            long ttfb = 0;
            Dictionary<string, string> headersDict = [];

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                stopwatch.Restart();
                await StreamLogAsync(target.Id.ToString(), $"> [INIT] Attempting connection to {target.Url} (Attempt {attempt})...", stoppingToken);
                
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stoppingToken);

                    stopwatch.Stop();
                    ttfb = stopwatch.ElapsedMilliseconds;
                    statusCode = (int)response.StatusCode;

                    long tcpHandshake = Math.Max(1, ttfb / 3);
                    await StreamLogAsync(target.Id.ToString(), $"> [TCP] TCP Handshake established in {tcpHandshake}ms.", stoppingToken);

                    await StreamLogAsync(target.Id.ToString(), $"> [HTTP] Received {statusCode}. Initiating Sentinel Security Audit....", stoppingToken);

                    if (response.IsSuccessStatusCode)
                    {
                        status = UptimeStatus.Healthy;
                        errorMessage = null;

                        headersDict.Clear();
                        foreach (var header in response.Headers)
                        {
                            headersDict.TryAdd(header.Key, string.Join(", ", header.Value));
                        }

                        await using var stream = await response.Content.ReadAsStreamAsync(stoppingToken);
                        var buffer = new byte[8192];
                        _ = await stream.ReadAsync(buffer, stoppingToken);

                        break;
                    }

                    errorMessage = $"HTTP {(int)response.StatusCode}";
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    ttfb = stopwatch.ElapsedMilliseconds;
                    errorMessage = ex.Message;
                    await StreamLogAsync(target.Id.ToString(), $"> [ERR] Connection failed: {ex.Message}", stoppingToken);
                }

                if (attempt < 3 && status != UptimeStatus.Healthy)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
            }

            var latency = (int)ttfb;

            // Real certificate inspection (issuer, expiry, negotiated TLS version + handshake timing).
            // Null for plain-HTTP targets or when the TLS handshake can't be completed.
            var certInfo = await InspectCertificateAsync(target.Url, stoppingToken);
            if (certInfo is not null)
            {
                await StreamLogAsync(target.Id.ToString(),
                    $"> [TLS] {certInfo.TlsVersion} — issued by {certInfo.Issuer}, expires {certInfo.ExpiryUtc:yyyy-MM-dd}.", stoppingToken);
            }
            else if (target.Url.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            {
                await StreamLogAsync(target.Id.ToString(), "> [TLS] Certificate inspection failed.", stoppingToken);
            }

            // ponytail: handshake timing comes from a separate TLS probe connection, so initLag is
            // an approximation of serverless cold-start — good enough, and honest vs. the old hardcoded 50/50.
            long handshakeMs = certInfo?.HandshakeMs ?? 0;
            long initLag = Math.Max(0, ttfb - handshakeMs);
            bool isColdStart = initLag > 800;

            if (ttfb > 800)
            {
                await StreamLogAsync(target.Id.ToString(), "> [INIT] Cold start detected.", stoppingToken);
            }
            
            await StreamLogAsync(target.Id.ToString(), $"> [DTA] Temporal gap analyzed: {initLag}ms variance.", stoppingToken);

            if (status == UptimeStatus.Healthy)
            {
                target.FailureCount = 0;
            }
            else
            {
                target.FailureCount++;
            }

            if (target.FailureCount >= 3)
            {
                status = UptimeStatus.Quarantined;
                await StreamLogAsync(target.Id.ToString(), "> [AUTH-GUARD] Circuit tripped. Target quarantined to protect resource quota.", stoppingToken);
            }

            char securityGrade = target.CurrentSecurityGrade;

            await using (var targetConnection = await dataSource.OpenConnectionAsync(stoppingToken))
            {
                await using var logCommand = targetConnection.CreateCommand();
                logCommand.CommandText = @"
                    INSERT INTO ""UptimeLogs"" (""MonitorId"", ""StatusCode"", ""LatencyMs"", ""IsColdStart"", ""ErrorMessage"", ""Timestamp"")
                    VALUES ($1, $2, $3, $4, $5, $6)";
                
                logCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = target.Id });
                logCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = statusCode ?? (object)DBNull.Value });
                logCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = latency });
                logCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = isColdStart });
                logCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = errorMessage ?? (object)DBNull.Value });
                logCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = DateTime.UtcNow });
                
                await logCommand.ExecuteNonQueryAsync(stoppingToken);

                if (status == UptimeStatus.Healthy)
                {
                    securityGrade = CalculateSecurityGrade(headersDict, certInfo);
                    var rawHeadersJson = JsonSerializer.Serialize(headersDict, AppJsonSerializerContext.Default.DictionaryStringString);

                    await using var auditCommand = targetConnection.CreateCommand();
                    auditCommand.CommandText = @"
                        INSERT INTO ""SecurityAudits"" (""Id"", ""MonitorId"", ""HasCsp"", ""HasHsts"", ""HasXfo"", ""HasNosniff"", ""RawHeaders"", ""CreatedAt"", ""SslIssuer"", ""SslExpiryAt"", ""TlsVersion"")
                        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)";

                    auditCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.NewGuid() });
                    auditCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = target.Id });
                    auditCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = headersDict.ContainsKey("Content-Security-Policy") });
                    auditCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = headersDict.ContainsKey("Strict-Transport-Security") });
                    auditCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = headersDict.ContainsKey("X-Frame-Options") });
                    auditCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = headersDict.ContainsKey("X-Content-Type-Options") });
                    auditCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = rawHeadersJson });
                    auditCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = DateTime.UtcNow });
                    auditCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)certInfo?.Issuer ?? DBNull.Value });
                    auditCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)certInfo?.ExpiryUtc ?? DBNull.Value });
                    auditCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)certInfo?.TlsVersion ?? DBNull.Value });

                    await auditCommand.ExecuteNonQueryAsync(stoppingToken);

                    if (target.CurrentSecurityGrade != 'U' && securityGrade > target.CurrentSecurityGrade)
                    {
                        await alertManager.ProcessSecurityAlertAsync(target, securityGrade, webhookUrl, stoppingToken);
                    }

                    if (certInfo is not null && certInfo.ExpiryUtc <= DateTime.UtcNow.AddDays(14))
                    {
                        await alertManager.ProcessSslExpiryAlertAsync(target, certInfo.ExpiryUtc, webhookUrl, stoppingToken);
                    }
                }

                await using var updateCommand = targetConnection.CreateCommand();
                updateCommand.CommandText = @"
                    UPDATE ""MonitorTargets""
                    SET ""CurrentUptimeStatus"" = $1,
                        ""CurrentSecurityGrade"" = $2,
                        ""UpdatedAt"" = $3,
                        ""FailureCount"" = $5
                    WHERE ""Id"" = $4";
                
                updateCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = (int)status });
                updateCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = securityGrade.ToString() });
                updateCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = DateTime.UtcNow });
                updateCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = target.Id });
                updateCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = target.FailureCount });
                
                await updateCommand.ExecuteNonQueryAsync(stoppingToken);
            }

            bool uptimeStateChanged = target.CurrentUptimeStatus != status;
            
            target.CurrentUptimeStatus = status;
            target.CurrentSecurityGrade = securityGrade;
            target.UpdatedAt = DateTime.UtcNow;

            if (uptimeStateChanged && status != UptimeStatus.Quarantined)
            {
                await alertManager.ProcessUptimeAlertAsync(target, status, webhookUrl, stoppingToken);
            }

            var update = new PulseUpdate(target.Id, status, latency);
            await hubContext.Clients.Group(target.UserId.ToString()).SendAsync("ReceivePulse", update, stoppingToken);
        }
    }

    private static char CalculateSecurityGrade(Dictionary<string, string> headers, CertInfo? cert)
    {
        int score = 0;

        if (headers.ContainsKey("Content-Security-Policy")) score++;
        if (headers.ContainsKey("Strict-Transport-Security")) score++;
        if (headers.ContainsKey("X-Frame-Options")) score++;
        if (headers.ContainsKey("X-Content-Type-Options")) score++;

        // Certificate present, valid, and not expiring within 14 days.
        if (cert is not null && cert.ExpiryUtc > DateTime.UtcNow.AddDays(14)) score++;
        // Modern TLS (1.2 or 1.3); plain-HTTP and deprecated TLS lose this point.
        if (cert is not null && cert.TlsVersion is "TLS 1.2" or "TLS 1.3") score++;

        // ponytail: 6-signal -> A-F bands are a tuning knob; adjust thresholds as grading policy evolves.
        return score switch
        {
            6 => 'A',
            5 => 'B',
            4 => 'C',
            3 => 'D',
            2 => 'E',
            _ => 'F'
        };
    }

    private sealed record CertInfo(string Issuer, DateTime ExpiryUtc, string TlsVersion, long HandshakeMs);

    // Opens a raw TLS connection to read the server certificate. The validation callback accepts all
    // certificates on purpose: a monitor must inspect expired/invalid certs to grade them, rather than
    // refuse the handshake. This is scoped to this probe only, not general outbound TLS.
    private static async Task<CertInfo?> InspectCertificateAsync(string url, CancellationToken stoppingToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 443;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            var ct = timeoutCts.Token;

            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(host, port, ct);

            await using var sslStream = new SslStream(tcpClient.GetStream(), leaveInnerStreamOpen: false);

            var stopwatch = Stopwatch.StartNew();
            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }, ct);
            stopwatch.Stop();

            if (sslStream.RemoteCertificate is not X509Certificate2 cert)
            {
                return null;
            }

            return new CertInfo(
                ParseIssuer(cert.Issuer),
                cert.NotAfter.ToUniversalTime(),
                MapTlsVersion(sslStream.SslProtocol),
                stopwatch.ElapsedMilliseconds);
        }
        catch
        {
            return null;
        }
    }

    // Distinguished name like "CN=R3, O=Let's Encrypt, C=US" -> prefer Organization, fall back to CN.
    private static string ParseIssuer(string distinguishedName)
    {
        foreach (var prefix in (ReadOnlySpan<string>)["O=", "CN="])
        {
            foreach (var part in distinguishedName.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed[prefix.Length..].Trim();
                }
            }
        }
        return distinguishedName;
    }

    private static string MapTlsVersion(SslProtocols protocol) => protocol switch
    {
        SslProtocols.Tls13 => "TLS 1.3",
        SslProtocols.Tls12 => "TLS 1.2",
#pragma warning disable SYSLIB0039
        SslProtocols.Tls11 => "TLS 1.1",
        SslProtocols.Tls => "TLS 1.0",
#pragma warning restore SYSLIB0039
        _ => protocol.ToString()
    };
}
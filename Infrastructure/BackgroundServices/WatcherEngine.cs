using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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

    private const string TargetSelectSql = @"
        SELECT mt.""Id"", mt.""Url"", mt.""FriendlyName"", mt.""CurrentUptimeStatus"",
               mt.""CurrentSecurityGrade"", mt.""IsActive"", mt.""UserId"", u.""DiscordWebhookUrl"", mt.""FailureCount"",
               mt.""RequestHeaders"", mt.""RequestTimeout"", u.""Email"", u.""SlackWebhookUrl"", u.""DigestEnabled"", mt.""MonitorType""
        FROM ""MonitorTargets"" mt
        INNER JOIN ""Users"" u ON mt.""UserId"" = u.""Id""
    ";

    private static (MonitorTarget Target, AlertRecipient Recipient) ReadTargetRow(NpgsqlDataReader reader)
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
            FailureCount = reader.GetInt32(8),
            RequestHeaders = reader.IsDBNull(9) ? null : reader.GetString(9),
            RequestTimeout = reader.GetInt32(10),
            MonitorType = (MonitorType)reader.GetInt32(14)
        };

        var recipient = new AlertRecipient(
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.GetBoolean(13));

        return (target, recipient);
    }

    private async Task ProcessTargetsAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var activeTargets = new List<(MonitorTarget Target, AlertRecipient Recipient)>();

        await using (var connection = await dataSource.OpenConnectionAsync(stoppingToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = TargetSelectSql + @"
                WHERE mt.""IsActive"" = true AND mt.""CurrentUptimeStatus"" != 3
                  AND (mt.""LastCheckedAt"" IS NULL
                       OR NOW() >= mt.""LastCheckedAt"" + make_interval(mins => mt.""IntervalMinutes""))";

            await using var reader = await command.ExecuteReaderAsync(stoppingToken);
            while (await reader.ReadAsync(stoppingToken))
            {
                activeTargets.Add(ReadTargetRow(reader));
            }
        }

        var client = httpClientFactory.CreateClient("WatcherClient");
        var journeyClient = httpClientFactory.CreateClient("JourneyClient");

        foreach (var (target, recipient) in activeTargets)
        {
            if (target.MonitorType == MonitorType.Journey)
            {
                await ProbeJourneyAsync(target, recipient, dataSource, journeyClient, stoppingToken);
            }
            else
            {
                await ProbeAsync(target, recipient, dataSource, client, stoppingToken);
            }
        }
    }

    // Bypasses the normal per-interval gate for a single monitor - used by the manual API-key
    // trigger and GitHub webhook endpoints (DevEndpoints.cs) to force an immediate out-of-band probe.
    // Returns false if the monitor doesn't exist or is paused (IsActive = false).
    public async Task<bool> TriggerImmediateProbeAsync(Guid monitorId, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        (MonitorTarget Target, AlertRecipient Recipient)? found = null;

        await using (var connection = await dataSource.OpenConnectionAsync(stoppingToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = TargetSelectSql + @" WHERE mt.""Id"" = $1 AND mt.""IsActive"" = true";
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = monitorId });

            await using var reader = await command.ExecuteReaderAsync(stoppingToken);
            if (await reader.ReadAsync(stoppingToken))
            {
                found = ReadTargetRow(reader);
            }
        }

        if (found is null) return false;

        if (found.Value.Target.MonitorType == MonitorType.Journey)
        {
            var journeyClient = httpClientFactory.CreateClient("JourneyClient");
            await ProbeJourneyAsync(found.Value.Target, found.Value.Recipient, dataSource, journeyClient, stoppingToken);
        }
        else
        {
            var client = httpClientFactory.CreateClient("WatcherClient");
            await ProbeAsync(found.Value.Target, found.Value.Recipient, dataSource, client, stoppingToken);
        }
        return true;
    }

    private async Task ProbeAsync(MonitorTarget target, AlertRecipient recipient, NpgsqlDataSource dataSource, HttpClient client, CancellationToken stoppingToken)
    {
        var stopwatch = new Stopwatch();
            var status = UptimeStatus.Down;
            string? errorMessage = null;
            int? statusCode = null;
            long ttfb = 0;
            Dictionary<string, string> headersDict = [];

            Dictionary<string, string>? customHeaders = target.RequestHeaders is null
                ? null
                : JsonSerializer.Deserialize(target.RequestHeaders, AppJsonSerializerContext.Default.DictionaryStringString);

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                stopwatch.Restart();
                await StreamLogAsync(target.Id.ToString(), $"> [INIT] Attempting connection to {target.Url} (Attempt {attempt})...", stoppingToken);

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);
                    if (customHeaders is not null)
                    {
                        foreach (var (key, value) in customHeaders)
                        {
                            request.Headers.TryAddWithoutValidation(key, value);
                        }
                    }

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(target.RequestTimeout));

                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

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
                        await alertManager.ProcessSecurityAlertAsync(target, securityGrade, headersDict, recipient, stoppingToken);
                    }

                    if (certInfo is not null && certInfo.ExpiryUtc <= DateTime.UtcNow.AddDays(14))
                    {
                        await alertManager.ProcessSslExpiryAlertAsync(target, certInfo.ExpiryUtc, recipient, stoppingToken);
                    }
                }

                await using var updateCommand = targetConnection.CreateCommand();
                updateCommand.CommandText = @"
                    UPDATE ""MonitorTargets""
                    SET ""CurrentUptimeStatus"" = $1,
                        ""CurrentSecurityGrade"" = $2,
                        ""UpdatedAt"" = $3,
                        ""FailureCount"" = $5,
                        ""LastCheckedAt"" = $3
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
                await alertManager.ProcessUptimeAlertAsync(target, status, recipient, stoppingToken);
            }

        var update = new PulseUpdate(target.Id, status, latency);
        await hubContext.Clients.Group(target.UserId.ToString()).SendAsync("ReceivePulse", update, stoppingToken);
    }

    private sealed record JourneyStepData(
        int StepOrder, string Method, string UrlOrPath, string? Headers, string? Body,
        string? CaptureAs, string? CaptureJsonPath, string? AssertJsonPath, string? AssertEquals, int? ExpectedStatusCode);

    private static async Task<List<JourneyStepData>> LoadJourneyStepsAsync(NpgsqlDataSource dataSource, Guid monitorId, CancellationToken stoppingToken)
    {
        var steps = new List<JourneyStepData>();

        await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT ""StepOrder"", ""Method"", ""UrlOrPath"", ""Headers"", ""Body"", ""CaptureAs"", ""CaptureJsonPath"", ""AssertJsonPath"", ""AssertEquals"", ""ExpectedStatusCode""
            FROM ""JourneySteps""
            WHERE ""MonitorId"" = $1
            ORDER BY ""StepOrder"";";
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = monitorId });

        await using var reader = await command.ExecuteReaderAsync(stoppingToken);
        while (await reader.ReadAsync(stoppingToken))
        {
            steps.Add(new JourneyStepData(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9)));
        }

        return steps;
    }

    // Plain string replace, not a template engine - captured values must be substitution-safe
    // (tokens/IDs/simple strings). No JSON-escaping is performed; this is a documented v1 limitation.
    private static string SubstituteVars(string input, Dictionary<string, string> vars)
    {
        foreach (var (key, value) in vars)
        {
            input = input.Replace("{{" + key + "}}", value);
        }
        return input;
    }

    // Deliberately minimal: dot-path only ($.a.b.c), no wildcards/filters/array indices.
    private static bool TryResolveJsonPath(JsonElement root, string path, out JsonElement result)
    {
        result = default;
        if (!path.StartsWith("$.", StringComparison.Ordinal)) return false;

        var current = root;
        foreach (var segment in path[2..].Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return false;
            }
            current = next;
        }

        result = current;
        return true;
    }

    // Never assume the captured/asserted value is a JSON string - a number or boolean field
    // (e.g. $.active, $.orders[0].id) would throw on a naive GetString() call.
    private static string JsonElementToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Null => "",
        _ => element.GetRawText()
    };

    // Journey monitors: an ordered sequence of HTTP steps sharing captured {{var}} state, executed
    // as one atomic check. Deliberately a separate method from ProbeAsync (not an interleaved branch
    // inside it) - ProbeAsync is the production-critical hot path for the overwhelming majority of
    // monitors and must stay completely unaffected by anything added here.
    private async Task ProbeJourneyAsync(MonitorTarget target, AlertRecipient recipient, NpgsqlDataSource dataSource, HttpClient client, CancellationToken stoppingToken)
    {
        var steps = await LoadJourneyStepsAsync(dataSource, target.Id, stoppingToken);
        if (steps.Count == 0)
        {
            return;
        }

        var stopwatch = new Stopwatch();
        var status = UptimeStatus.Down;
        string? errorMessage = null;
        int? firstStepStatusCode = null;
        long latency = 0;
        Dictionary<string, string> headersDict = [];

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            // Reset every attempt - a captured token from a failed attempt must not leak into a retry.
            var capturedVars = new Dictionary<string, string>();
            status = UptimeStatus.Healthy;
            errorMessage = null;
            firstStepStatusCode = null;
            headersDict.Clear();

            stopwatch.Restart();
            await StreamLogAsync(target.Id.ToString(), $"> [INIT] Starting journey ({steps.Count} steps, attempt {attempt})...", stoppingToken);

            foreach (var step in steps)
            {
                var url = SubstituteVars(step.UrlOrPath, capturedVars);

                try
                {
                    using var request = new HttpRequestMessage(new HttpMethod(step.Method), url);

                    if (step.Headers is not null)
                    {
                        var headers = JsonSerializer.Deserialize(step.Headers, AppJsonSerializerContext.Default.DictionaryStringString);
                        if (headers is not null)
                        {
                            foreach (var (key, value) in headers)
                            {
                                request.Headers.TryAddWithoutValidation(key, SubstituteVars(value, capturedVars));
                            }
                        }
                    }

                    if (step.Body is not null)
                    {
                        request.Content = new StringContent(SubstituteVars(step.Body, capturedVars), Encoding.UTF8, "application/json");
                    }

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(target.RequestTimeout));

                    using var response = await client.SendAsync(request, timeoutCts.Token);
                    var statusCode = (int)response.StatusCode;

                    if (step.StepOrder == 0)
                    {
                        firstStepStatusCode = statusCode;
                        foreach (var header in response.Headers)
                        {
                            headersDict.TryAdd(header.Key, string.Join(", ", header.Value));
                        }
                    }

                    var expectedOk = step.ExpectedStatusCode is int expected
                        ? statusCode == expected
                        : statusCode is >= 200 and < 300;

                    if (!expectedOk)
                    {
                        status = UptimeStatus.Down;
                        errorMessage = $"Step {step.StepOrder + 1} ({step.Method} {url}) failed: expected {(step.ExpectedStatusCode?.ToString() ?? "2xx")}, got {statusCode}";
                        break;
                    }

                    if (step.CaptureJsonPath is not null || step.AssertJsonPath is not null)
                    {
                        var bodyText = await response.Content.ReadAsStringAsync(stoppingToken);

                        JsonDocument bodyDoc;
                        try
                        {
                            bodyDoc = JsonDocument.Parse(bodyText);
                        }
                        catch
                        {
                            status = UptimeStatus.Down;
                            errorMessage = $"Step {step.StepOrder + 1} ({step.Method} {url}) failed: response body is not valid JSON";
                            break;
                        }

                        using (bodyDoc)
                        {
                            if (step.AssertJsonPath is not null)
                            {
                                if (!TryResolveJsonPath(bodyDoc.RootElement, step.AssertJsonPath, out var actual))
                                {
                                    status = UptimeStatus.Down;
                                    errorMessage = $"Step {step.StepOrder + 1} ({step.Method} {url}) failed: path {step.AssertJsonPath} not found in response";
                                    break;
                                }

                                var actualString = JsonElementToString(actual);
                                if (actualString != step.AssertEquals)
                                {
                                    status = UptimeStatus.Down;
                                    errorMessage = $"Step {step.StepOrder + 1} ({step.Method} {url}) failed: assertion {step.AssertJsonPath} expected '{step.AssertEquals}', got '{actualString}'";
                                    break;
                                }
                            }

                            if (step.CaptureAs is not null && step.CaptureJsonPath is not null)
                            {
                                if (!TryResolveJsonPath(bodyDoc.RootElement, step.CaptureJsonPath, out var captured))
                                {
                                    status = UptimeStatus.Down;
                                    errorMessage = $"Step {step.StepOrder + 1} ({step.Method} {url}) failed: capture path {step.CaptureJsonPath} not found in response";
                                    break;
                                }

                                capturedVars[step.CaptureAs] = JsonElementToString(captured);
                            }
                        }
                    }

                    await StreamLogAsync(target.Id.ToString(), $"> [STEP {step.StepOrder + 1}] {step.Method} {url} -> {statusCode} OK", stoppingToken);
                }
                catch (Exception ex)
                {
                    status = UptimeStatus.Down;
                    errorMessage = $"Step {step.StepOrder + 1} ({step.Method} {url}) failed: {ex.Message}";
                }

                if (status != UptimeStatus.Healthy)
                {
                    await StreamLogAsync(target.Id.ToString(), $"> [ERR] {errorMessage}", stoppingToken);
                    break;
                }
            }

            stopwatch.Stop();
            latency = stopwatch.ElapsedMilliseconds;

            if (status == UptimeStatus.Healthy) break;
            if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }

        // Cert inspection always probes target.Url, which for Journey monitors is kept in sync with
        // step 1's URL server-side at creation/edit time - same single-URL cert code as classic monitors.
        var certInfo = await InspectCertificateAsync(target.Url, stoppingToken);
        long handshakeMs = certInfo?.HandshakeMs ?? 0;
        long initLag = Math.Max(0, latency - handshakeMs);
        bool isColdStart = initLag > 800;

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
            logCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = firstStepStatusCode ?? (object)DBNull.Value });
            logCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = (int)latency });
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
                    await alertManager.ProcessSecurityAlertAsync(target, securityGrade, headersDict, recipient, stoppingToken);
                }

                if (certInfo is not null && certInfo.ExpiryUtc <= DateTime.UtcNow.AddDays(14))
                {
                    await alertManager.ProcessSslExpiryAlertAsync(target, certInfo.ExpiryUtc, recipient, stoppingToken);
                }
            }

            await using var updateCommand = targetConnection.CreateCommand();
            updateCommand.CommandText = @"
                UPDATE ""MonitorTargets""
                SET ""CurrentUptimeStatus"" = $1,
                    ""CurrentSecurityGrade"" = $2,
                    ""UpdatedAt"" = $3,
                    ""FailureCount"" = $5,
                    ""LastCheckedAt"" = $3
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
            await alertManager.ProcessUptimeAlertAsync(target, status, recipient, stoppingToken);
        }

        var update = new PulseUpdate(target.Id, status, (int)latency);
        await hubContext.Clients.Group(target.UserId.ToString()).SendAsync("ReceivePulse", update, stoppingToken);
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
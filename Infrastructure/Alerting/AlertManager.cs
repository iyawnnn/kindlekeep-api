using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using KindleKeep.Api.Core.Entities;
using KindleKeep.Api.Core.Enums;
using KindleKeep.Api.Infrastructure.Data;
using KindleKeep.Api.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KindleKeep.Api.Infrastructure.Alerting;

public record DiscordPayload(string content);
public record SlackPayload(string text);
public record ResendPayload(string from, string[] to, string subject, string html);

// WatcherEngine constructs MonitorTarget via raw ADO.NET without a populated User navigation
// property (Native AOT hot-path convention - see CLAUDE.md), so alert dispatch needs the
// recipient's channels/settings passed explicitly rather than read off target.User.
public record AlertRecipient(string? Email, string? DiscordWebhookUrl, string? SlackWebhookUrl, bool DigestEnabled);

public class AlertManager(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory)
{
    private readonly ConcurrentDictionary<string, DateTime> _activeIncidents = new();

    public async Task ProcessUptimeAlertAsync(MonitorTarget target, UptimeStatus newStatus, AlertRecipient recipient, CancellationToken stoppingToken)
    {
        var rawFingerprint = $"{target.Id}:Uptime:{newStatus}";
        var hash = ComputeHash(rawFingerprint);

        if (_activeIncidents.ContainsKey(hash) && newStatus == UptimeStatus.Down)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<KindleDbContext>();

        if (newStatus == UptimeStatus.Healthy)
        {
            var downHash = ComputeHash($"{target.Id}:Uptime:{UptimeStatus.Down}");
            _activeIncidents.TryRemove(downHash, out _);

            // Raw SQL, not LINQ: with <PublishAot>true</PublishAot> dynamic query compilation is
            // disabled even under `dotnet run`, so any un-precompiled EF LINQ query throws at
            // runtime ("Query wasn't precompiled and dynamic code isn't supported with NativeAOT").
            await dbContext.Database.ExecuteSqlRawAsync(
                "UPDATE \"AlertIncidents\" SET \"IsResolved\" = true, \"ResolvedAt\" = {0} WHERE \"IncidentHash\" = {1} AND \"IsResolved\" = false",
                [DateTime.UtcNow, downHash],
                cancellationToken: stoppingToken);
        }
        else
        {
            _activeIncidents.TryAdd(hash, DateTime.UtcNow);

            dbContext.AlertIncidents.Add(new AlertIncident
            {
                MonitorId = target.Id,
                IncidentHash = hash,
                IncidentType = "Uptime",
                IsResolved = false
            });
            await dbContext.SaveChangesAsync(stoppingToken);
        }

        await DispatchDiscordAlertAsync(target, newStatus, recipient.DiscordWebhookUrl, stoppingToken);
        await DispatchSlackAlertAsync(target, newStatus, recipient.SlackWebhookUrl, stoppingToken);
    }

    public async Task ProcessSecurityAlertAsync(MonitorTarget target, char newGrade, Dictionary<string, string> headers, AlertRecipient recipient, CancellationToken stoppingToken)
    {
        var rawFingerprint = $"{target.Id}:Security:{newGrade}";
        var hash = ComputeHash(rawFingerprint);

        if (_activeIncidents.ContainsKey(hash))
        {
            return;
        }

        _activeIncidents.TryAdd(hash, DateTime.UtcNow);

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<KindleDbContext>();

        dbContext.AlertIncidents.Add(new AlertIncident
        {
            MonitorId = target.Id,
            IncidentHash = hash,
            IncidentType = "Security",
            IsResolved = false
        });
        await dbContext.SaveChangesAsync(stoppingToken);

        await DispatchResendAlertAsync(target, newGrade, headers, recipient, stoppingToken);
    }

    public async Task ProcessSslExpiryAlertAsync(MonitorTarget target, DateTime expiryUtc, AlertRecipient recipient, CancellationToken stoppingToken)
    {
        // Fingerprint on the cert's expiry month so a given certificate alerts once, not every probe.
        // A renewed cert has a new ExpiryUtc -> new hash -> a fresh alert.
        var hash = ComputeHash($"{target.Id}:SslExpiry:{expiryUtc:yyyy-MM}");

        if (_activeIncidents.ContainsKey(hash))
        {
            return;
        }

        _activeIncidents.TryAdd(hash, DateTime.UtcNow);

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<KindleDbContext>();

        dbContext.AlertIncidents.Add(new AlertIncident
        {
            MonitorId = target.Id,
            IncidentHash = hash,
            IncidentType = "SslExpiry",
            IsResolved = false
        });
        await dbContext.SaveChangesAsync(stoppingToken);

        var daysLeft = (int)Math.Floor((expiryUtc - DateTime.UtcNow).TotalDays);
        var urgency = daysLeft < 0 ? "has EXPIRED" : $"expires in {daysLeft} day(s)";

        await DispatchSslExpiryDiscordAsync(target, urgency, expiryUtc, recipient.DiscordWebhookUrl, stoppingToken);
        await DispatchSslExpirySlackAsync(target, urgency, expiryUtc, recipient.SlackWebhookUrl, stoppingToken);
        if (!recipient.DigestEnabled)
        {
            await DispatchSslExpiryEmailAsync(target, urgency, expiryUtc, recipient.Email, stoppingToken);
        }
    }

    private async Task DispatchSslExpiryDiscordAsync(MonitorTarget target, string urgency, DateTime expiryUtc, string? webhookUrl, CancellationToken stoppingToken)
    {
        var targetWebhook = webhookUrl ?? configuration["Alerting:DiscordWebhookUrl"];
        if (string.IsNullOrEmpty(targetWebhook)) return;

        var client = httpClientFactory.CreateClient("DiscordClient");
        var payload = new DiscordPayload($"SSL certificate for **{target.FriendlyName}** ({target.Url}) {urgency} (expiry: {expiryUtc:yyyy-MM-dd}).");

        await client.PostAsJsonAsync(targetWebhook, payload, AppJsonSerializerContext.Default.DiscordPayload, stoppingToken);
    }

    private async Task DispatchSslExpirySlackAsync(MonitorTarget target, string urgency, DateTime expiryUtc, string? slackWebhookUrl, CancellationToken stoppingToken)
    {
        var targetWebhook = slackWebhookUrl ?? configuration["Alerting:SlackWebhookUrl"] ?? Environment.GetEnvironmentVariable("KK_ALERTING_SLACK_WEBHOOK_URL");
        if (string.IsNullOrEmpty(targetWebhook)) return;

        var client = httpClientFactory.CreateClient("SlackClient");
        var payload = new SlackPayload($"SSL certificate for *{target.FriendlyName}* ({target.Url}) {urgency} (expiry: {expiryUtc:yyyy-MM-dd}).");

        await client.PostAsJsonAsync(targetWebhook, payload, AppJsonSerializerContext.Default.SlackPayload, stoppingToken);
    }

    private async Task DispatchSslExpiryEmailAsync(MonitorTarget target, string urgency, DateTime expiryUtc, string? email, CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(email)) return;

        await SendResendEmailAsync(
            email,
            $"SSL Certificate Expiry: {target.FriendlyName}",
            $"<p>The SSL certificate for <strong>{target.FriendlyName}</strong> ({target.Url}) {urgency}.</p><p>Expiry date: <strong>{expiryUtc:yyyy-MM-dd}</strong>.</p>",
            stoppingToken);
    }

    private async Task DispatchDiscordAlertAsync(MonitorTarget target, UptimeStatus status, string? webhookUrl, CancellationToken stoppingToken)
    {
        var targetWebhook = webhookUrl ?? configuration["Alerting:DiscordWebhookUrl"];
        if (string.IsNullOrEmpty(targetWebhook)) return;

        var client = httpClientFactory.CreateClient("DiscordClient");
        var payload = new DiscordPayload($"Monitor **{target.FriendlyName}** ({target.Url}) status changed to **{status}**.");
        
        await client.PostAsJsonAsync(targetWebhook, payload, AppJsonSerializerContext.Default.DiscordPayload, stoppingToken);
    }

    private async Task DispatchSlackAlertAsync(MonitorTarget target, UptimeStatus status, string? slackWebhookUrl, CancellationToken stoppingToken)
    {
        var targetWebhook = slackWebhookUrl ?? configuration["Alerting:SlackWebhookUrl"] ?? Environment.GetEnvironmentVariable("KK_ALERTING_SLACK_WEBHOOK_URL");
        if (string.IsNullOrEmpty(targetWebhook)) return;

        var client = httpClientFactory.CreateClient("SlackClient");
        var payload = new SlackPayload($"Monitor *{target.FriendlyName}* ({target.Url}) status changed to *{status}*.");

        await client.PostAsJsonAsync(targetWebhook, payload, AppJsonSerializerContext.Default.SlackPayload, stoppingToken);
    }

    private async Task DispatchResendAlertAsync(MonitorTarget target, char grade, Dictionary<string, string> headers, AlertRecipient recipient, CancellationToken stoppingToken)
    {
        if (recipient.DigestEnabled) return;

        var apiKey = configuration["Alerting:ResendApiKey"];
        if (string.IsNullOrEmpty(apiKey)) return;

        var email = recipient.Email;
        if (string.IsNullOrEmpty(email)) return;

        var fromEmail = configuration["Alerting:FromEmail"] ?? "onboarding@resend.dev";

        var missingHeaders = new List<string>();
        if (!headers.ContainsKey("Content-Security-Policy")) missingHeaders.Add("Content-Security-Policy");
        if (!headers.ContainsKey("Strict-Transport-Security")) missingHeaders.Add("Strict-Transport-Security");
        if (!headers.ContainsKey("X-Frame-Options")) missingHeaders.Add("X-Frame-Options");
        if (!headers.ContainsKey("X-Content-Type-Options")) missingHeaders.Add("X-Content-Type-Options");

        var blueprintHtml = "";
        if (missingHeaders.Count > 0)
        {
            var platform = BlueprintGenerator.DetectPlatform(headers);
            var snippet = BlueprintGenerator.GenerateSnippet(platform, missingHeaders);
            var escapedSnippet = System.Net.WebUtility.HtmlEncode(snippet);
            blueprintHtml = $"<p>Detected platform: <strong>{platform}</strong>. Paste this in to fix it:</p><pre style=\"background:#f4f4f5;padding:12px;overflow-x:auto;\">{escapedSnippet}</pre>";
        }

        await SendResendEmailAsync(
            email,
            $"Security Grade Regression: {target.FriendlyName}",
            $"<p>The security grade for <strong>{target.FriendlyName}</strong> has dropped to <strong>{grade}</strong>.</p>{blueprintHtml}",
            stoppingToken);
    }

    // Called once/day by DigestService for opted-in users - summarizes open/recent incidents into
    // a single email instead of the instant per-incident Resend path (which DigestEnabled suppresses).
    public async Task DispatchDigestEmailAsync(string email, List<(string MonitorName, string IncidentType, DateTime CreatedAt)> incidents, CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(email) || incidents.Count == 0) return;

        var rows = string.Join("", incidents.Select(i =>
            $"<tr><td style=\"padding:6px 12px;\">{System.Net.WebUtility.HtmlEncode(i.MonitorName)}</td>" +
            $"<td style=\"padding:6px 12px;\">{System.Net.WebUtility.HtmlEncode(i.IncidentType)}</td>" +
            $"<td style=\"padding:6px 12px;\">{i.CreatedAt:yyyy-MM-dd HH:mm} UTC</td></tr>"));

        var html = $"<p>Your daily KindleKeep digest — {incidents.Count} incident(s) in the last 24 hours:</p>" +
            $"<table style=\"border-collapse:collapse;\">{rows}</table>";

        await SendResendEmailAsync(email, $"KindleKeep Daily Digest: {incidents.Count} incident(s)", html, stoppingToken);
    }

    // Escalation tier for Uptime-Down incidents that go unacknowledged - reuses the existing
    // Resend path rather than a new HTTP call. Called by EscalationService, not the hot
    // WatcherEngine -> ProcessUptimeAlertAsync dispatch path.
    public async Task DispatchEscalationEmailAsync(string monitorName, string monitorUrl, string? email, CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(email)) return;

        await SendResendEmailAsync(
            email,
            $"Unacknowledged Incident: {monitorName}",
            $"<p><strong>{monitorName}</strong> ({monitorUrl}) has been down and remains unacknowledged. An initial alert was already sent via Discord/Slack.</p>",
            stoppingToken);
    }

    internal async Task SendResendEmailAsync(string toEmail, string subject, string html, CancellationToken stoppingToken)
    {
        var apiKey = configuration["Alerting:ResendApiKey"];
        if (string.IsNullOrEmpty(apiKey)) return;

        var fromEmail = configuration["Alerting:FromEmail"] ?? "onboarding@resend.dev";

        var client = httpClientFactory.CreateClient("ResendClient");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var payload = new ResendPayload(fromEmail, [toEmail], subject, html);

        await client.PostAsJsonAsync("https://api.resend.com/emails", payload, AppJsonSerializerContext.Default.ResendPayload, stoppingToken);
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
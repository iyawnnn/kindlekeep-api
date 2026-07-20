using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using KindleKeep.Api.Core.Entities;
using KindleKeep.Api.Core.Enums;
using KindleKeep.Api.Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KindleKeep.Api.Infrastructure.Alerting;

public record DiscordPayload(string content);
public record ResendPayload(string from, string[] to, string subject, string html);

public class AlertManager(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory)
{
    private readonly ConcurrentDictionary<string, DateTime> _activeIncidents = new();

    public async Task ProcessUptimeAlertAsync(MonitorTarget target, UptimeStatus newStatus, string? webhookUrl, CancellationToken stoppingToken)
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

            var openIncident = dbContext.AlertIncidents.FirstOrDefault(i => i.IncidentHash == downHash && !i.IsResolved);
            if (openIncident != null)
            {
                openIncident.IsResolved = true;
                openIncident.ResolvedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(stoppingToken);
            }
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

        await DispatchDiscordAlertAsync(target, newStatus, webhookUrl, stoppingToken);
    }

    public async Task ProcessSecurityAlertAsync(MonitorTarget target, char newGrade, string? webhookUrl, CancellationToken stoppingToken)
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

        await DispatchResendAlertAsync(target, newGrade, stoppingToken);
    }

    public async Task ProcessSslExpiryAlertAsync(MonitorTarget target, DateTime expiryUtc, string? webhookUrl, CancellationToken stoppingToken)
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

        await DispatchSslExpiryDiscordAsync(target, urgency, expiryUtc, webhookUrl, stoppingToken);
        await DispatchSslExpiryEmailAsync(target, urgency, expiryUtc, stoppingToken);
    }

    private async Task DispatchSslExpiryDiscordAsync(MonitorTarget target, string urgency, DateTime expiryUtc, string? webhookUrl, CancellationToken stoppingToken)
    {
        var targetWebhook = webhookUrl ?? configuration["Alerting:DiscordWebhookUrl"];
        if (string.IsNullOrEmpty(targetWebhook)) return;

        var client = httpClientFactory.CreateClient("DiscordClient");
        var payload = new DiscordPayload($"SSL certificate for **{target.FriendlyName}** ({target.Url}) {urgency} (expiry: {expiryUtc:yyyy-MM-dd}).");

        await client.PostAsJsonAsync(targetWebhook, payload, AppJsonSerializerContext.Default.DiscordPayload, stoppingToken);
    }

    private async Task DispatchSslExpiryEmailAsync(MonitorTarget target, string urgency, DateTime expiryUtc, CancellationToken stoppingToken)
    {
        var apiKey = configuration["Alerting:ResendApiKey"];
        if (string.IsNullOrEmpty(apiKey)) return;

        var email = target.User?.Email;
        if (string.IsNullOrEmpty(email)) return;

        var fromEmail = configuration["Alerting:FromEmail"] ?? "onboarding@resend.dev";

        var client = httpClientFactory.CreateClient("ResendClient");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var payload = new ResendPayload(
            fromEmail,
            [email],
            $"SSL Certificate Expiry: {target.FriendlyName}",
            $"<p>The SSL certificate for <strong>{target.FriendlyName}</strong> ({target.Url}) {urgency}.</p><p>Expiry date: <strong>{expiryUtc:yyyy-MM-dd}</strong>.</p>"
        );

        await client.PostAsJsonAsync("https://api.resend.com/emails", payload, AppJsonSerializerContext.Default.ResendPayload, stoppingToken);
    }

    private async Task DispatchDiscordAlertAsync(MonitorTarget target, UptimeStatus status, string? webhookUrl, CancellationToken stoppingToken)
    {
        var targetWebhook = webhookUrl ?? configuration["Alerting:DiscordWebhookUrl"];
        if (string.IsNullOrEmpty(targetWebhook)) return;

        var client = httpClientFactory.CreateClient("DiscordClient");
        var payload = new DiscordPayload($"Monitor **{target.FriendlyName}** ({target.Url}) status changed to **{status}**.");
        
        await client.PostAsJsonAsync(targetWebhook, payload, AppJsonSerializerContext.Default.DiscordPayload, stoppingToken);
    }

    private async Task DispatchResendAlertAsync(MonitorTarget target, char grade, CancellationToken stoppingToken)
    {
        var apiKey = configuration["Alerting:ResendApiKey"];
        if (string.IsNullOrEmpty(apiKey)) return;

        var email = target.User?.Email;
        if (string.IsNullOrEmpty(email)) return;

        var fromEmail = configuration["Alerting:FromEmail"] ?? "onboarding@resend.dev";

        var client = httpClientFactory.CreateClient("ResendClient");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var payload = new ResendPayload(
            fromEmail,
            [email],
            $"Security Grade Regression: {target.FriendlyName}",
            $"<p>The security grade for <strong>{target.FriendlyName}</strong> has dropped to <strong>{grade}</strong>.</p>"
        );

        await client.PostAsJsonAsync("https://api.resend.com/emails", payload, AppJsonSerializerContext.Default.ResendPayload, stoppingToken);
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
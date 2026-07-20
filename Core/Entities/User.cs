using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using KindleKeep.Api.Core.Enums;

namespace KindleKeep.Api.Core.Entities;

public class User
{
    public Guid Id { get; set; }
    public required string ExternalId { get; set; }
    public required AuthProvider AuthProvider { get; set; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? DiscordWebhookUrl { get; set; }
    public bool EnableEmailNotifications { get; set; } = true;

    [JsonIgnore]
    public ICollection<MonitorTarget> Monitors { get; set; } = new List<MonitorTarget>();
}